using BillAgent.Worker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace BillAgent.Worker.Services.Reconciler;

/// <summary>
/// The tool surface Agent B (the Reconciler) can call.
///
/// Each public method decorated with [KernelFunction] is exposed to the LLM
/// as a callable tool by Semantic Kernel's auto function-calling.
///
/// Lifecycle: a NEW instance is constructed per payment being reconciled.
/// The current payment id is implicit context — the LLM does not have to pass
/// it in every call. This keeps the tool signatures the agent sees small.
///
/// Outcome recording: every reconciliation pass calls exactly one of
///   MarkBillPaid / FlagBillNeedsReview / FlagPaymentUnmatched
/// to record its decision. The Outcome field is set by those three terminal tools
/// so the caller can read what the agent decided after the chat completes.
/// </summary>
public class ReconcilerToolset
{
    /// <summary>Defense-in-depth: agent confidence below this on a "matched" call
    /// is downgraded to "needs review" in .NET. Matches our prompt rubric so the
    /// agent normally never trips it — this is the safety net, not the main path.</summary>
    public const double MatchConfidenceThreshold = 0.85;

    private readonly BillAgentDbContext _db;
    private readonly ILogger _logger;
    private readonly Guid _paymentId;

    public ReconcilerOutcome Outcome { get; private set; } = ReconcilerOutcome.Pending();

    public ReconcilerToolset(BillAgentDbContext db, ILogger logger, Guid paymentId)
    {
        _db = db;
        _logger = logger;
        _paymentId = paymentId;
    }

    // ── retrieval tool ───────────────────────────────────────────────────────

    [KernelFunction("list_pending_bills_for")]
    [System.ComponentModel.Description(
        "Returns up to 10 PENDING bills matching the filter. Use loose filters: vendor is a substring " +
        "(use a core token like 'Колекторски' not the full company name); period is YYYY-MM or null; " +
        "amountMin/amountMax bracket the payment amount with a small window. Returns id, vendor, period, " +
        "amount, currency, due_date, reference, related_references for the agent to reason over.")]
    public async Task<IReadOnlyList<BillCandidate>> ListPendingBillsForAsync(
        [System.ComponentModel.Description("Substring to match against the vendor column (case-insensitive). Use the most distinctive core token.")]
        string vendor,
        [System.ComponentModel.Description("YYYY-MM, or null/empty to skip the period filter.")]
        string? period,
        [System.ComponentModel.Description("Lower bound on amount, inclusive. Match the payment amount with a small tolerance.")]
        decimal amountMin,
        [System.ComponentModel.Description("Upper bound on amount, inclusive.")]
        decimal amountMax)
    {
        // Tight tool query, loose agent reasoning (per DECISIONS.md Day 7).
        // SQL narrows; LLM picks. The agent can re-query with looser bounds if it wants.
        var query = _db.Bills
            .Where(b => b.Status == BillStatus.Pending)
            .Where(b => EF.Functions.ILike(b.Vendor, $"%{vendor}%"))
            .Where(b => b.Amount >= amountMin && b.Amount <= amountMax);

        if (!string.IsNullOrWhiteSpace(period))
            query = query.Where(b => b.Period == period);

        var rows = await query
            .OrderByDescending(b => b.CreatedAt)
            .Take(10)
            .Select(b => new BillCandidate(
                b.Id,
                b.Vendor,
                b.Period,
                b.Amount,
                b.Currency,
                b.DueDate,
                b.Reference,
                b.RelatedReferences))
            .ToListAsync();

        _logger.LogInformation(
            "Reconciler.ListPendingBillsFor(vendor~'{Vendor}', period='{Period}', amount=[{Min}..{Max}]) → {Count} candidate(s)",
            vendor, period ?? "any", amountMin, amountMax, rows.Count);

        return rows;
    }

    // ── outcome-recording tools (terminal — exactly one of these must be called) ──

    [KernelFunction("mark_bill_paid")]
    [System.ComponentModel.Description(
        "Record that this payment pays the given bill. Sets bill.status='paid' and payment.matched_bill_id=bill.id. " +
        "Call ONLY when confidence is at least 0.85. For weaker matches use flag_bill_needs_review instead.")]
    public async Task<string> MarkBillPaidAsync(
        [System.ComponentModel.Description("Id of the bill returned by list_pending_bills_for.")]
        Guid billId,
        [System.ComponentModel.Description("Your self-assessed confidence 0.0-1.0 that this is the correct match.")]
        double confidence,
        [System.ComponentModel.Description("One-sentence reasoning: vendor/period/amount/reference signals you used.")]
        string reasoning)
    {
        if (confidence < MatchConfidenceThreshold)
        {
            // Defense-in-depth: the agent is told 0.85 in its prompt; if it ignores
            // that and tries to mark a low-confidence match, we don't mutate `bills`.
            // We record the attempt as "needs review" instead so a human sees it.
            _logger.LogWarning(
                "Reconciler attempted mark_bill_paid with confidence={Conf} < {Threshold}. Downgraded to needs_review.",
                confidence, MatchConfidenceThreshold);
            return await FlagBillNeedsReviewAsync(billId, confidence,
                $"[auto-downgraded from mark_bill_paid: confidence {confidence:0.00} below {MatchConfidenceThreshold:0.00}] {reasoning}");
        }

        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == billId);
        if (bill is null)
            return $"ERROR: bill {billId} not found. Call list_pending_bills_for first and pass an id from the result.";

        var payment = await _db.Payments.FirstAsync(p => p.Id == _paymentId);

        bill.Status = BillStatus.Paid;
        payment.MatchedBillId = bill.Id;

        await _db.SaveChangesAsync();

        Outcome = ReconcilerOutcome.Matched(bill.Id, confidence, reasoning);
        _logger.LogInformation(
            "Reconciler MATCHED payment {PaymentId} → bill {BillId} ({Vendor} {Amount} {Currency}), conf={Conf:0.00}",
            _paymentId, bill.Id, bill.Vendor, bill.Amount, bill.Currency, confidence);

        return $"OK — bill {bill.Id} marked paid, payment.matched_bill_id set.";
    }

    [KernelFunction("flag_bill_needs_review")]
    [System.ComponentModel.Description(
        "Record an AMBIGUOUS match: a candidate bill looks related but you cannot confirm with confidence ≥ 0.85. " +
        "Sets bill.status='needs_review'. Leaves payment.matched_bill_id NULL so a human can complete the match. " +
        "Use this when multiple candidates plausibly match, or when one matches with material disagreement on period/vendor/amount.")]
    public async Task<string> FlagBillNeedsReviewAsync(
        [System.ComponentModel.Description("Id of the most-likely candidate bill.")]
        Guid billId,
        [System.ComponentModel.Description("Your self-assessed confidence 0.0-1.0.")]
        double confidence,
        [System.ComponentModel.Description("One-sentence reasoning: which signals matched, which didn't, why you can't commit.")]
        string reasoning)
    {
        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == billId);
        if (bill is null)
            return $"ERROR: bill {billId} not found.";

        bill.Status = BillStatus.NeedsReview;
        // Note: we DO NOT set payment.matched_bill_id — a human will choose during review.

        await _db.SaveChangesAsync();

        Outcome = ReconcilerOutcome.Ambiguous(bill.Id, confidence, reasoning);
        _logger.LogInformation(
            "Reconciler flagged AMBIGUOUS payment {PaymentId} ↔ bill {BillId}, conf={Conf:0.00}: {Reason}",
            _paymentId, bill.Id, confidence, reasoning);

        return $"OK — bill {bill.Id} flagged needs_review.";
    }

    [KernelFunction("flag_payment_unmatched")]
    [System.ComponentModel.Description(
        "Record that NO bill in the system plausibly matches this payment. " +
        "Payment row stays as-is with matched_bill_id NULL. A human (or Agent C) will surface it later.")]
    public Task<string> FlagPaymentUnmatchedAsync(
        [System.ComponentModel.Description("Why nothing matched: e.g. 'no pending bills for this vendor in any recent period'.")]
        string reasoning)
    {
        Outcome = ReconcilerOutcome.Unmatched(reasoning);
        _logger.LogInformation(
            "Reconciler flagged UNMATCHED payment {PaymentId}: {Reason}",
            _paymentId, reasoning);
        return Task.FromResult("OK — payment recorded as unmatched.");
    }
}

/// <summary>
/// Lightweight bill projection returned by list_pending_bills_for.
/// Kept narrow — Agent B doesn't need confidence/reasoning/status/timestamps to decide a match.
/// </summary>
public record BillCandidate(
    Guid Id,
    string Vendor,
    string? Period,
    decimal Amount,
    string Currency,
    DateOnly? DueDate,
    string? Reference,
    string[] RelatedReferences);
