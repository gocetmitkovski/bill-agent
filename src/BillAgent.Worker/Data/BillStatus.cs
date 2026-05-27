namespace BillAgent.Worker.Data;

/// <summary>
/// Mirrors the CHECK constraint on bills.status in 0001_init.sql.
///
/// We keep these as string constants (not a C# enum) because:
///   1. Postgres column is TEXT with CHECK, not an ENUM type — easier to evolve later.
///   2. JSONB raw_extraction / Sheets export read these strings directly without conversion.
///   3. Agent B's tool calls return these as JSON strings; no enum-serialization friction.
/// </summary>
public static class BillStatus
{
    public const string Pending     = "pending";
    public const string Paid        = "paid";
    public const string NeedsReview = "needs_review";
}

/// <summary>
/// Mirrors BillExtraction.Kind values produced by Agent A.
/// Stored verbatim into email_log.kind so we can audit every message we've seen.
/// </summary>
public static class EmailKind
{
    public const string Invoice             = "invoice";
    public const string PaymentConfirmation = "payment_confirmation";
    public const string Other               = "other";
}
