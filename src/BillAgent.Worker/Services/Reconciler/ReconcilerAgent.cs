using BillAgent.Worker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace BillAgent.Worker.Services.Reconciler;

#pragma warning disable SKEXP0070 // Google connector is experimental — accepted, documented in DECISIONS.md.
#pragma warning disable SKEXP0001 // FunctionChoiceBehavior.Auto() is in the experimental API surface.

/// <summary>
/// Agent B — the Reconciler.
///
/// For each payment with matched_bill_id IS NULL, runs ONE LLM session with three tools:
///   list_pending_bills_for(...)      — retrieve candidates
///   mark_bill_paid(...)              — terminal: matched (DB mutated)
///   flag_bill_needs_review(...)      — terminal: ambiguous (DB mutated to needs_review)
///   flag_payment_unmatched(...)      — terminal: nothing in DB to match against
///
/// The agent reads the payment, calls list_pending_bills_for one or more times to narrow,
/// then records its decision by calling exactly one terminal tool.
///
/// ── Why this is the agentic centerpiece of the thesis ─────────────────────────
/// Agent A is a pure extractor (no tools, single LLM call).
/// Agent B reasons over noisy real-world identifiers and writes state through tools.
/// The narrowness of the tool surface (4 methods) is itself a design claim: agents
/// should be deployed precisely where reasoning is the bottleneck, with the smallest
/// tool surface that captures the decision.
/// </summary>
public class ReconcilerAgent
{
    private const string ModelId = "gemini-2.5-flash";

    private readonly ILogger<ReconcilerAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly SheetsWriter _sheets;
    private readonly string _apiKey;

    public ReconcilerAgent(
        ILogger<ReconcilerAgent> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        SheetsWriter sheets,
        IConfiguration config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _services = services;
        _sheets = sheets;
        _apiKey = config["GEMINI_API_KEY"]
            ?? throw new InvalidOperationException("GEMINI_API_KEY missing.");
    }

    /// <summary>
    /// Sweep: find every payment with matched_bill_id IS NULL and reconcile each one.
    /// Returns outcomes in the order the payments were processed for the Worker to log.
    /// </summary>
    public async Task<IReadOnlyList<(Guid PaymentId, ReconcilerOutcome Outcome)>> SweepAsync(CancellationToken ct)
    {
        // One scope per sweep — within the sweep we open a fresh per-payment scope
        // so each reconciliation has its own DbContext lifetime (clean change-tracking).
        await using var listScope = _services.CreateAsyncScope();
        var listDb = listScope.ServiceProvider.GetRequiredService<BillAgentDbContext>();

        var unmatched = await listDb.Payments
            .Where(p => p.MatchedBillId == null)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Id)
            .ToListAsync(ct);

        _logger.LogInformation("Reconciler sweep: {Count} unmatched payment(s) to process.", unmatched.Count);

        var results = new List<(Guid, ReconcilerOutcome)>();
        foreach (var paymentId in unmatched)
        {
            var outcome = await ReconcileOneAsync(paymentId, ct);
            results.Add((paymentId, outcome));
        }
        return results;
    }

    /// <summary>
    /// Reconcile a single payment. Opens a per-payment DbContext scope, builds a fresh
    /// Kernel with the tool plugin bound to this payment, runs the chat loop, returns
    /// the outcome the toolset recorded.
    /// </summary>
    public async Task<ReconcilerOutcome> ReconcileOneAsync(Guid paymentId, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillAgentDbContext>();

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null)
        {
            _logger.LogWarning("ReconcileOne: payment {Id} not found.", paymentId);
            return ReconcilerOutcome.Unmatched("payment not found");
        }
        if (payment.MatchedBillId is not null)
        {
            _logger.LogInformation("ReconcileOne: payment {Id} already matched — skipping.", paymentId);
            return ReconcilerOutcome.Matched(payment.MatchedBillId.Value, 1.0, "already matched on a previous sweep");
        }

        var toolset = new ReconcilerToolset(db, _loggerFactory.CreateLogger<ReconcilerToolset>(), paymentId);

        var kernel = Kernel.CreateBuilder()
            .AddGoogleAIGeminiChatCompletion(ModelId, _apiKey)
            .Build();
        kernel.Plugins.AddFromObject(toolset, pluginName: "reconciler");

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(payment));

        var settings = new GeminiPromptExecutionSettings
        {
            Temperature = 0.1,
            // Auto function calling: SK runs the tool-call loop transparently —
            // the agent's tool calls execute against the toolset and their results
            // are fed back into the conversation until the model stops calling tools.
            FunctionChoiceBehavior = Microsoft.SemanticKernel.FunctionChoiceBehavior.Auto(),
        };

        // The chat completes when the model emits a turn without a tool call.
        // By that point the toolset.Outcome should have been set by one of the terminal tools.
        try
        {
            await CallWithRetryAsync(
                () => chat.GetChatMessageContentAsync(history, settings, kernel, ct),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconciler chat threw for payment {Id}. Outcome will remain pending.", paymentId);
        }

        if (toolset.Outcome.Status == ReconcilerStatus.Pending)
        {
            _logger.LogWarning(
                "Reconciler did not record an outcome for payment {Id} — treating as unmatched.", paymentId);
            return ReconcilerOutcome.Unmatched("agent finished without calling a terminal tool");
        }

        // ── Project outcome to the Sheet ─────────────────────────────────────
        // Agent decides; system projects. Sheets is a derived view of Postgres
        // (Day 5 design); we update the Status cell of the affected bill row
        // OUTSIDE the agent's tool surface so the four narrow tools stay narrow.
        // Failure here logs and continues — Postgres is the source of truth.
        if (toolset.Outcome.BillId is Guid billId)
        {
            try
            {
                var bill = await db.Bills
                    .Where(b => b.Id == billId)
                    .Select(b => new { b.GmailMessageId, b.Status })
                    .FirstOrDefaultAsync(ct);
                if (bill is not null)
                    await _sheets.UpdateBillStatusAsync(bill.GmailMessageId, bill.Status, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Sheet status update failed for bill {BillId}; DB row is authoritative.", billId);
            }
        }

        return toolset.Outcome;
    }

    // ── retry on rate-limit, same shape as BillExtractor ─────────────────────
    private async Task<T> CallWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var delays = new[] { 5, 15, 30 };
        for (int attempt = 0; attempt <= delays.Length; attempt++)
        {
            try { return await action(); }
            catch (Microsoft.SemanticKernel.HttpOperationException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                      && attempt < delays.Length)
            {
                _logger.LogWarning("Gemini rate-limited (429). Backing off {Delay}s (attempt {Attempt}/{Max}).",
                    delays[attempt], attempt + 1, delays.Length);
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct);
            }
        }
        return await action();
    }

    // ── prompt ───────────────────────────────────────────────────────────────
    //
    // Design choices, captured here so they live next to the prompt that encodes them:
    //
    // 1. The matching heuristic is described in NATURAL LANGUAGE, not in code.
    //    This is the thesis claim made explicit: vendor-string variation, period
    //    adjacency, amount tolerance, reference corroboration — these are reasoning
    //    rules, not pattern-matching rules. The agent's prompt is the spec.
    //
    // 2. The confidence rubric is calibrated to the .NET-side threshold (0.85).
    //    If you change one, change the other. The two layers should agree, so the
    //    defensive downgrade in MarkBillPaidAsync only fires when the model misbehaves.
    //
    // 3. The few-shot examples use REAL anonymised data shapes from the test inbox
    //    (Колекторски and Телекабел) so the model sees patterns it will encounter live.
    //    These are not synthetic — they are the exact failure modes the thesis is about.
    private const string SystemPrompt = """
        You are Agent B — the Reconciler — in a utility-bill tracking system.

        Your job: given ONE payment confirmation, decide whether it pays a known invoice
        already in the system. Record your decision by calling exactly one of three
        terminal tools: mark_bill_paid, flag_bill_needs_review, or flag_payment_unmatched.

        TOOLS YOU CAN CALL:
        - list_pending_bills_for(vendor, period, amountMin, amountMax)
            Retrieves up to 10 PENDING candidate bills matching the filter.
            Vendor is a substring (case-insensitive). Use a CORE TOKEN of the vendor name
            (e.g. "Колекторски" not the full "ЈП Колекторски систем - Скопје"), because
            invoices and payment confirmations often write the vendor name differently.
            Period is "YYYY-MM" or null/empty to skip the period filter.
            Amount window: use payment.amount ± 1.00 first; widen only if no candidates.
            You may call this MORE THAN ONCE with different filters.

        - mark_bill_paid(billId, confidence, reasoning)
            Terminal. Call when ONE candidate clearly matches. Requires confidence ≥ 0.85.

        - flag_bill_needs_review(billId, confidence, reasoning)
            Terminal. Call when a candidate plausibly matches but you cannot commit at ≥ 0.85
            (period gap, vendor variant heavily ambiguous, amount close but not exact, or
            multiple candidates plausible). Records the best-guess bill as needs_review.

        - flag_payment_unmatched(reasoning)
            Terminal. Call when list_pending_bills_for returned NO candidates, or none of
            the candidates plausibly match.

        MATCHING HEURISTIC — read carefully:

        VENDOR. Utility companies write their name differently across invoice and payment.
        "ЈП Колекторски систем" (invoice) and "ЈП Колекторски систем - Скопје" (payment
        confirmation) are the same vendor. Use semantic identity, not string equality.
        When filtering with list_pending_bills_for, use the most distinctive CORE TOKEN.

        PERIOD. Invoices have a period (YYYY-MM). Payments sometimes don't. If the invoice
        period is N and the payment paid_date falls in month N, N+1, or N+2, that's a
        normal payment timeline — not a "no" signal. Different periods with no other
        explanation IS a strong "no" signal.

        AMOUNT. Utility bills are usually paid in full. EXACT amount match is the strongest
        single signal. 63.00 vs 63.0 is NOT a difference. 63.00 vs 63.50 IS a difference
        worth a confidence hit (partial payment? wrong bill?).

        REFERENCES. Invoice numbers (e.g. "04-2026-АГ7262-0") sometimes appear in the
        payment's reference or related_references. WHEN THEY DO, that's near-certain
        confirmation. Their ABSENCE is not a "no" signal — payment confirmations often
        only carry bank transaction codes (e.g. "NLB-WEB-...-69f8b319...") which never
        appear on the invoice side.

        CONFIDENCE RUBRIC — be calibrated, not optimistic:
        - 0.95+: vendor semantic match, period or paid_date aligns, amount exact, and
                 a shared reference token appears in both payment.reference/related and
                 bill.reference/related.
        - 0.85-0.95: vendor + amount exact + period plausible; references don't disagree
                 (i.e. no shared invoice number, but no contradicting tokens either).
        - 0.70-0.85: plausible but friction (period off, vendor heavily ambiguous, amount
                 close-not-exact). DO NOT call mark_bill_paid; call flag_bill_needs_review.
        - below 0.70: very weak. Prefer flag_bill_needs_review on the best candidate, or
                 flag_payment_unmatched if no candidate stands out.

        WORKED EXAMPLES (illustrative — match the shape of real data you will see):

        Example A — clean match (call mark_bill_paid, conf 0.97):
          Payment: vendor="Телекабел", amount=1406.00 MKD, paid_date=2026-05-04,
                   reference="04-2026-АГ7262-0", related=["АГ7262","NLB-WEB-...-69f8b319..."]
          list_pending_bills_for("Телекабел", null, 1405.00, 1407.00) → 1 bill:
            { vendor="Телекабел", period="2026-04", amount=1406.00, reference="04-2026-АГ7262-0",
              related=["АГ7262-0"], due_date=2026-05-20 }
          Reasoning: vendor exact, amount exact, paid_date in same month as period+1,
                     invoice reference "04-2026-АГ7262-0" appears verbatim in payment.reference.
                     → call mark_bill_paid(billId, 0.97, "...").

        Example B — vendor variant, period missing on payment (call mark_bill_paid, conf 0.90):
          Payment: vendor="ЈП Колекторски систем - Скопје", amount=63.00, paid_date=2025-06-14,
                   period=null, reference="25051450182", related=["25165KcYBAmoesm9996"]
          list_pending_bills_for("Колекторски", null, 62.00, 64.00) → 1 bill:
            { vendor="ЈП Колекторски систем", period="2025-05", amount=63.00,
              reference="25051450182", related=["1450182"], due_date=2025-06-30 }
          Reasoning: vendor semantic match (one is substring of the other), amount exact,
                     paid_date 2025-06-14 within period 2025-05 + 1 month grace,
                     reference "25051450182" identical on both sides.
                     → call mark_bill_paid(billId, 0.95, "...").

        Example C — ambiguous (call flag_bill_needs_review, conf 0.7):
          Payment: vendor="EVN", amount=1200.00, paid_date=2026-04-15, reference=null.
          list_pending_bills_for("EVN", null, 1199.00, 1201.00) → 2 bills:
            { period="2026-02", amount=1200.00 } AND { period="2026-03", amount=1200.00 }
          Reasoning: amount exact for both, vendor exact for both, no reference to disambiguate.
                     → call flag_bill_needs_review on whichever is older, conf 0.7.

        Example D — no candidates (call flag_payment_unmatched):
          Payment: vendor="Some New Vendor", amount=500.00.
          list_pending_bills_for("Some New Vendor", null, 499.00, 501.00) → []
          → call flag_payment_unmatched("no pending bills for this vendor at this amount").

        Rules:
        - You MUST call exactly one terminal tool before finishing.
        - Think briefly out loud before the terminal call. Keep it short — committee will
          read these reasoning strings; conciseness is a virtue.
        - You may call list_pending_bills_for up to 3 times if your first filter returns nothing.
        """;

    private static string BuildUserMessage(Payment p)
    {
        var related = p.RelatedReferences is { Length: > 0 }
            ? string.Join(", ", p.RelatedReferences.Select(r => $"\"{r}\""))
            : "(none)";
        return $"""
            === PAYMENT TO RECONCILE ===
            payment_id:           {p.Id}
            vendor (as written):  {p.Vendor ?? "(null)"}
            amount:               {p.Amount} {p.Currency}
            paid_date:            {p.PaidDate?.ToString("yyyy-MM-dd") ?? "(null)"}
            reference:            {p.Reference ?? "(null)"}
            related_references:   {related}
            extraction confidence: {p.Confidence:0.00}
            extraction reasoning:  {p.Reasoning ?? "(none)"}

            Begin reconciliation now. Call list_pending_bills_for first, then exactly one
            terminal tool (mark_bill_paid / flag_bill_needs_review / flag_payment_unmatched).
            """;
    }
}

#pragma warning restore SKEXP0001
#pragma warning restore SKEXP0070
