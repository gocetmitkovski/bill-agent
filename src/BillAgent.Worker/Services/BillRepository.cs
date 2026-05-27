using System.Globalization;
using System.Text.Json;
using BillAgent.Worker.Data;
using Microsoft.EntityFrameworkCore;

namespace BillAgent.Worker.Services;

/// <summary>
/// Persistence layer. Wraps the DbContext so the Worker doesn't see EF directly,
/// and so we have ONE place that knows how to translate a BillExtraction into rows.
///
/// Idempotency contract:
///   - HasProcessedAsync is the cheap precheck the polling loop uses to skip emails.
///   - The UNIQUE constraint on gmail_message_id is the actual guarantee.
///     If two worker instances raced, one INSERT would throw — we catch and ignore.
///
/// Resolves DbContext from DI per-call so it works inside a singleton hosted service.
/// (DbContext itself is registered scoped; we open a scope each operation.)
/// </summary>
public class BillRepository
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BillRepository> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public BillRepository(IServiceProvider services, ILogger<BillRepository> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Has the worker already persisted anything for this Gmail message id?
    /// Checked in email_log because EVERY processed message lands there
    /// (even "other"/skipped ones).
    /// </summary>
    public async Task<bool> HasProcessedAsync(string gmailMessageId, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillAgentDbContext>();
        return await db.EmailLog.AnyAsync(e => e.GmailMessageId == gmailMessageId, ct);
    }

    /// <summary>
    /// Persist one processed email.
    ///   - Always writes an email_log row (the audit trail).
    ///   - If kind == "invoice" → writes a bills row (status=pending, or needs_review if confidence low).
    ///   - If kind == "payment_confirmation" → writes a payments row (matched_bill_id stays null; Agent B fills it Day 7).
    ///   - If kind == "other" → email_log only.
    ///
    /// All three writes happen in a single EF transaction so we never end up with
    /// a bills row without its email_log row.
    ///
    /// Returns the persisted Bill if one was written (for downstream Sheets sync),
    /// otherwise null. Returns null on race-on-UNIQUE too — that path already
    /// wrote the sheet row in the winning iteration.
    /// </summary>
    public async Task<Bill?> PersistAsync(
        string gmailMessageId,
        string? subject,
        string? sender,
        BillExtraction extraction,
        CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillAgentDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rawJson = JsonSerializer.Serialize(extraction, JsonOpts);

        db.EmailLog.Add(new EmailLogEntry
        {
            GmailMessageId    = gmailMessageId,
            Subject           = subject,
            Sender            = sender,
            Kind              = extraction.Kind,
            RawExtractionJson = rawJson,
        });

        Bill? insertedBill = null;

        switch (extraction.Kind)
        {
            case EmailKind.Invoice:
                if (TryBuildBill(gmailMessageId, extraction, out var bill, out var why))
                {
                    db.Bills.Add(bill);
                    insertedBill = bill;
                }
                else
                    _logger.LogWarning("Skipping bills insert for {Id}: {Why}", gmailMessageId, why);
                break;

            case EmailKind.PaymentConfirmation:
                if (TryBuildPayment(gmailMessageId, extraction, out var pay, out var why2))
                    db.Payments.Add(pay);
                else
                    _logger.LogWarning("Skipping payments insert for {Id}: {Why}", gmailMessageId, why2);
                break;

            case EmailKind.Other:
                // email_log row only — nothing else to store.
                break;

            default:
                _logger.LogWarning("Unknown extraction kind '{Kind}' for {Id} — logging only.", extraction.Kind, gmailMessageId);
                break;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            // After SaveChanges, EF populates server-generated columns (CreatedAt, UpdatedAt)
            // from the RETURNING clause, so insertedBill is ready to hand to SheetsWriter.
            return insertedBill;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another worker iteration won the race. Idempotency held — log and move on.
            _logger.LogInformation("Race on gmail_message_id={Id} (UNIQUE constraint) — already persisted.", gmailMessageId);
            await tx.RollbackAsync(ct);
            return null;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the minimum-required fields for an invoice and constructs the row.
    /// Confidence < 0.7 → status flips to needs_review (Agent B will surface it Day 10).
    /// </summary>
    private static bool TryBuildBill(string msgId, BillExtraction e, out Bill bill, out string why)
    {
        bill = null!;
        if (string.IsNullOrWhiteSpace(e.Vendor))  { why = "vendor missing";   return false; }
        if (e.Amount is null or <= 0)             { why = "amount missing/invalid"; return false; }
        if (string.IsNullOrWhiteSpace(e.Currency)){ why = "currency missing"; return false; }

        bill = new Bill
        {
            GmailMessageId    = msgId,
            Vendor            = e.Vendor!.Trim(),
            Amount            = e.Amount!.Value,
            Currency          = e.Currency!.Trim().ToUpperInvariant(),
            DueDate           = ParseIsoDate(e.DueDate),
            Period            = e.Period?.Trim(),
            Reference         = e.Reference?.Trim(),
            RelatedReferences = e.RelatedReferences ?? Array.Empty<string>(),
            Confidence        = e.Confidence,
            Reasoning         = e.Reasoning,
            Status            = e.Confidence < 0.7 ? BillStatus.NeedsReview : BillStatus.Pending,
        };
        why = "";
        return true;
    }

    private static bool TryBuildPayment(string msgId, BillExtraction e, out Payment pay, out string why)
    {
        pay = null!;
        if (e.Amount is null or <= 0)             { why = "amount missing/invalid"; return false; }
        if (string.IsNullOrWhiteSpace(e.Currency)){ why = "currency missing"; return false; }

        pay = new Payment
        {
            GmailMessageId    = msgId,
            Vendor            = e.Vendor?.Trim(),
            Amount            = e.Amount!.Value,
            Currency          = e.Currency!.Trim().ToUpperInvariant(),
            PaidDate          = ParseIsoDate(e.PaidDate),
            Reference         = e.Reference?.Trim(),
            RelatedReferences = e.RelatedReferences ?? Array.Empty<string>(),
            Confidence        = e.Confidence,
            Reasoning         = e.Reasoning,
            MatchedBillId     = null,  // Agent B sets this on Day 7
        };
        why = "";
        return true;
    }

    /// <summary>The LLM is told to return ISO 8601 dates; defensively parse anyway.</summary>
    private static DateOnly? ParseIsoDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Detect Postgres UNIQUE-violation SQLSTATE 23505 wrapped by EF.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}
