using System.Text.Json;
using BillAgent.Worker.Services;
using BillAgent.Worker.Services.Reconciler;

namespace BillAgent.Worker;

public class Worker : BackgroundService
{
    private const string Label = "utility-bills";
    private const int MaxMessagesToList = 10;

    private readonly ILogger<Worker> _logger;
    private readonly GmailReader _gmail;
    private readonly PdfTextExtractor _pdf;
    private readonly BillExtractor _extractor;
    private readonly BillRepository _repo;
    private readonly SheetsWriter _sheets;
    private readonly ReconcilerAgent _reconciler;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(
        ILogger<Worker> logger,
        GmailReader gmail,
        PdfTextExtractor pdf,
        BillExtractor extractor,
        BillRepository repo,
        SheetsWriter sheets,
        ReconcilerAgent reconciler,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _gmail = gmail;
        _pdf = pdf;
        _extractor = extractor;
        _repo = repo;
        _sheets = sheets;
        _reconciler = reconciler;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting OAuth handshake with Gmail...");
            await _gmail.InitializeAsync(stoppingToken);

            _logger.LogInformation("Starting OAuth handshake with Sheets...");
            await _sheets.InitializeAsync(stoppingToken);

            _logger.LogInformation("Listing messages with label '{Label}'...", Label);
            var stubs = await _gmail.ListMessagesByLabelAsync(Label, MaxMessagesToList, stoppingToken);
            _logger.LogInformation("Found {Count} message(s).", stubs.Count);

            foreach (var stub in stubs)
            {
                // Idempotency precheck — if we've already processed this Gmail message id,
                // skip the LLM call entirely. (Saves Gemini quota AND ensures deterministic re-runs.)
                if (await _repo.HasProcessedAsync(stub.Id, stoppingToken))
                {
                    _logger.LogInformation("Skipping {Id}: already in email_log.", stub.Id);
                    continue;
                }

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
                }
            }

            // ── Agent B sweep ────────────────────────────────────────────────
            // After ingestion is done, run the reconciler over every unmatched payment.
            // This is the "sweep mode" design choice from DECISIONS.md: Agent A reads
            // email, Agent B reconciles, they communicate through the database.
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine("  Agent B — Reconciler sweep");
            Console.WriteLine("════════════════════════════════════════");
            var sweepResults = await _reconciler.SweepAsync(stoppingToken);
            foreach (var (paymentId, outcome) in sweepResults)
            {
                Console.WriteLine($"  payment={paymentId}  status={outcome.Status}" +
                                  (outcome.BillId is null ? "" : $"  bill={outcome.BillId}") +
                                  (outcome.Confidence is null ? "" : $"  conf={outcome.Confidence:0.00}"));
                Console.WriteLine($"    reason: {outcome.Reasoning}");
            }

            _logger.LogInformation("Day 7 happy path complete. Shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker failed");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
