using System.Text.Json;
using BillAgent.Worker.Services;
using BillAgent.Worker.Services.Reconciler;
using BillAgent.Worker.Services.Telegram;

namespace BillAgent.Worker;

public class Worker : BackgroundService
{
    private const string Label = "utility-bills";
    private const int MaxMessagesToList = 10;

    // Default poll interval: once per day. Override with BILLAGENT_POLL_INTERVAL
    // in .env. Accepts any TimeSpan string ("00:00:30" = 30s, "00:05:00" = 5min,
    // "1.00:00:00" = 1 day) or a bare integer interpreted as seconds.
    // Demo-friendly: lower it to seconds for the defense, leave it at a day in prod.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromDays(1);

    private readonly ILogger<Worker> _logger;
    private readonly GmailReader _gmail;
    private readonly PdfTextExtractor _pdf;
    private readonly BillExtractor _extractor;
    private readonly BillRepository _repo;
    private readonly SheetsWriter _sheets;
    private readonly ReconcilerAgent _reconciler;
    private readonly TelegramNotifier _telegram;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(
        ILogger<Worker> logger,
        GmailReader gmail,
        PdfTextExtractor pdf,
        BillExtractor extractor,
        BillRepository repo,
        SheetsWriter sheets,
        ReconcilerAgent reconciler,
        TelegramNotifier telegram,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _gmail = gmail;
        _pdf = pdf;
        _extractor = extractor;
        _repo = repo;
        _sheets = sheets;
        _reconciler = reconciler;
        _telegram = telegram;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── One-time setup (outside the poll loop) ──────────────────────────
        // OAuth handshake happens once per process. Both clients cache their
        // refresh token to disk; subsequent ticks reuse the same authenticated
        // client instance. If either of these fails, the worker cannot do
        // anything useful, so we log critical and let the host exit.
        try
        {
            _logger.LogInformation("Starting OAuth handshake with Gmail...");
            await _gmail.InitializeAsync(stoppingToken);

            _logger.LogInformation("Starting OAuth handshake with Sheets...");
            await _sheets.InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "OAuth initialization failed. Worker cannot start.");
            _lifetime.StopApplication();
            return;
        }

        var interval = ResolvePollInterval();
        _logger.LogInformation("Polling every {Interval}. Press Ctrl+C to stop.", interval);

        // ── Poll loop ───────────────────────────────────────────────────────
        // Each tick runs the same ingest + sweep that Day 7 established.
        // A failure inside one tick is logged and swallowed; the next tick
        // runs after the delay. Cancellation propagates through stoppingToken.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — break out of the loop.
                break;
            }
            catch (Exception ex)
            {
                // Anything else: log and keep going. The whole point of the
                // loop is that transient errors (Gmail 5xx, Postgres reconnect,
                // Gemini rate limit) don't take the worker down.
                _logger.LogError(ex, "Tick failed. Sleeping {Interval} before next attempt.", interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Worker stopping (cancellation requested).");
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Tick: listing messages with label '{Label}'...", Label);
        var stubs = await _gmail.ListMessagesByLabelAsync(Label, MaxMessagesToList, stoppingToken);
        _logger.LogInformation("Found {Count} message(s).", stubs.Count);

        var newThisTick = 0;

        foreach (var stub in stubs)
        {
            // Idempotency precheck — if we've already processed this Gmail message id,
            // skip the LLM call entirely. (Saves Gemini quota AND ensures deterministic re-runs.)
            if (await _repo.HasProcessedAsync(stub.Id, stoppingToken))
            {
                // Debug, not Info — steady-state loop should not spam the log
                // with "already processed" lines every tick forever.
                _logger.LogDebug("Skipping {Id}: already in email_log.", stub.Id);
                continue;
            }

            newThisTick++;

            var full = await _gmail.GetMessageAsync(stub.Id, stoppingToken);
            var content = GmailReader.ExtractContent(full);

            // Concatenate PDF text from all PDF attachments (most invoices have just one).
            var pdfs = await _gmail.GetPdfAttachmentsAsync(full, stoppingToken);
            string? pdfText = null;
            if (pdfs.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (filename, bytes) in pdfs)
                    sb.AppendLine(_pdf.Extract(bytes, filename));
                pdfText = sb.ToString();
            }

            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine($"  Subject: {content.Subject}");
            Console.WriteLine($"  From:    {content.From}");
            Console.WriteLine($"  PDFs:    {pdfs.Count}");
            Console.WriteLine("────────── Agent A says ──────────");

            var extraction = await _extractor.ExtractAsync(content, pdfText, stoppingToken);
            var pretty = JsonSerializer.Serialize(extraction, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Don't \uXXXX-escape Cyrillic / non-ASCII — keep console output human-readable.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            Console.WriteLine(pretty);

            // Persist: email_log row always; bills/payments depending on kind.
            // Returns the inserted Bill (if any) for downstream Sheets sync.
            var inserted = await _repo.PersistAsync(stub.Id, content.Subject, content.From, extraction, stoppingToken);
            Console.WriteLine("──────────  persisted  ──────────");

            // Sheet sync runs OUTSIDE the DB transaction. Postgres is the source of truth;
            // the sheet is a derived view. If this throws, the bill is still safely in the DB
            // and can be re-synced manually. (See DECISIONS.md — outbox pattern is future work.)
            if (inserted is not null)
            {
                try
                {
                    await _sheets.AppendBillAsync(inserted, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sheets append failed for {Id} — DB row is intact; manual re-sync required.", stub.Id);
                }

                // ── Day 10: notify the operator via Telegram ─────────────────
                // Same projection-of-state-changes pattern as the Sheet:
                // Agent A decides; the system projects the decision onto
                // user-facing surfaces. The notifier is fail-soft — a Telegram
                // outage must not break ingest.
                var dueStr = inserted.DueDate.HasValue ? inserted.DueDate.Value.ToString("yyyy-MM-dd") : "no due date";
                await _telegram.SendAsync(
                    $"🧾 New invoice — {inserted.Vendor}, {inserted.Amount:0.00} {inserted.Currency} (due {dueStr})",
                    stoppingToken);
            }
        }

        // ── Agent B sweep ────────────────────────────────────────────────
        // After ingestion is done, run the reconciler over every unmatched payment.
        // Sweep runs EVERY tick (not just ticks with new messages), because a
        // payment confirmation that arrived before its invoice must be revisited
        // until the invoice catches up. The visual header is conditional on new
        // ingestion this tick so the steady-state log stays quiet.
        if (newThisTick > 0)
        {
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine("  Agent B — Reconciler sweep");
            Console.WriteLine("════════════════════════════════════════");
        }
        var sweepResults = await _reconciler.SweepAsync(stoppingToken);
        foreach (var (paymentId, outcome) in sweepResults)
        {
            Console.WriteLine($"  payment={paymentId}  status={outcome.Status}" +
                              (outcome.BillId is null ? "" : $"  bill={outcome.BillId}") +
                              (outcome.Confidence is null ? "" : $"  conf={outcome.Confidence:0.00}"));
            Console.WriteLine($"    reason: {outcome.Reasoning}");
        }

        _logger.LogInformation("Tick complete. {New} new message(s) ingested.", newThisTick);
    }

    private static TimeSpan ResolvePollInterval()
    {
        var raw = Environment.GetEnvironmentVariable("BILLAGENT_POLL_INTERVAL");
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultPollInterval;

        // Bare integer = seconds (ergonomic: BILLAGENT_POLL_INTERVAL=30).
        // IMPORTANT: try int BEFORE TimeSpan.TryParse — the latter parses "30"
        // as thirty days, which would silently turn a 30-second demo cadence
        // into a one-month sleep. Keep this ordering.
        if (int.TryParse(raw, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        // TimeSpan string = explicit (BILLAGENT_POLL_INTERVAL=00:00:30 or 1.00:00:00).
        if (TimeSpan.TryParse(raw, out var ts) && ts > TimeSpan.Zero)
            return ts;

        // Anything else: fall back to default and warn on stderr.
        Console.Error.WriteLine($"[warn] BILLAGENT_POLL_INTERVAL='{raw}' is not a valid TimeSpan or positive integer. Using default {DefaultPollInterval}.");
        return DefaultPollInterval;
    }
}
