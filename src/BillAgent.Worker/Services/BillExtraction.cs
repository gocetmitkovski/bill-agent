namespace BillAgent.Worker.Services;

/// <summary>
/// The structured output contract for Agent A (Extractor).
/// Whatever Agent A reads from an email/PDF, it produces ONE of these.
/// Agent B (Reconciler) consumes this shape and decides what to do.
///
/// Kept as a record so we can serialize/deserialize via System.Text.Json cleanly.
/// </summary>
public record BillExtraction(
    /// <summary>"invoice" | "payment_confirmation" | "other"</summary>
    string Kind,

    /// <summary>Utility company name as it appears in the email (e.g. "Vodovod Skopje", "EVN").</summary>
    string? Vendor,

    /// <summary>Total amount due (invoice) or paid (confirmation). Null if not found.</summary>
    decimal? Amount,

    /// <summary>ISO 4217 code. Macedonian bills are MKD.</summary>
    string? Currency,

    /// <summary>Due date for invoices (ISO 8601). Null for confirmations.</summary>
    string? DueDate,

    /// <summary>Payment date for confirmations (ISO 8601). Null for invoices.</summary>
    string? PaidDate,

    /// <summary>The billing period this covers, e.g. "2025-05" or "May 2025".</summary>
    string? Period,

    /// <summary>
    /// The single identifier most likely to appear on the *matching counterpart* document.
    /// For an INVOICE this is the invoice number (e.g. "25051450182", "04-2026-АГ7262-0").
    /// For a CONFIRMATION this should be the same invoice number IF the email mentions which
    /// invoice it paid; if the email only carries a bank transaction code, put that here.
    /// Picking the right primary reference is part of Agent A's reasoning, not a string match.
    /// </summary>
    string? Reference,

    /// <summary>
    /// All *other* identifiers Agent A noticed in the document — bank transaction IDs,
    /// merchant reference codes, customer numbers, internal IDs, etc.
    /// Agent B's reconciliation can search across these when the primary Reference doesn't match.
    /// Empty array if no additional identifiers were seen.
    /// </summary>
    string[] RelatedReferences,

    /// <summary>0.0 – 1.0. Agent A's self-assessed confidence in the extraction. Below 0.7 → Agent B flags for review.</summary>
    double Confidence,

    /// <summary>One sentence: why the agent classified and extracted as it did. Goes into email_log for audit.</summary>
    string Reasoning);
