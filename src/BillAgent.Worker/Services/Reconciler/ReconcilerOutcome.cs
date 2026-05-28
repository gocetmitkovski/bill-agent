namespace BillAgent.Worker.Services.Reconciler;

/// <summary>
/// The decision Agent B recorded for a single payment.
///
/// One of four states:
///   - Pending: agent has not yet recorded an outcome (initial state; should not persist).
///   - Matched: agent called mark_bill_paid; the DB has been mutated.
///   - Ambiguous: agent called flag_bill_needs_review; bill.status='needs_review'.
///   - Unmatched: agent called flag_payment_unmatched; nothing mutated.
///
/// Stored as a single shape so the caller (Worker) can render any outcome uniformly
/// without switching on type.
/// </summary>
public record ReconcilerOutcome(
    ReconcilerStatus Status,
    Guid? BillId,
    double? Confidence,
    string Reasoning)
{
    public static ReconcilerOutcome Pending() =>
        new(ReconcilerStatus.Pending, null, null, "agent did not record an outcome");

    public static ReconcilerOutcome Matched(Guid billId, double confidence, string reason) =>
        new(ReconcilerStatus.Matched, billId, confidence, reason);

    public static ReconcilerOutcome Ambiguous(Guid billId, double confidence, string reason) =>
        new(ReconcilerStatus.Ambiguous, billId, confidence, reason);

    public static ReconcilerOutcome Unmatched(string reason) =>
        new(ReconcilerStatus.Unmatched, null, null, reason);
}

public enum ReconcilerStatus
{
    Pending,
    Matched,
    Ambiguous,
    Unmatched,
}
