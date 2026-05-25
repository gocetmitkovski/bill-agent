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

    /// <summary>Invoice / reference / transaction number — anything the vendor uses to identify this document.</summary>
    string? Reference,

    /// <summary>0.0 – 1.0. Agent A's self-assessed confidence in the extraction. Below 0.7 → Agent B flags for review.</summary>
    double Confidence,

    /// <summary>One sentence: why the agent classified and extracted as it did. Goes into email_log for audit.</summary>
    string Reasoning);
