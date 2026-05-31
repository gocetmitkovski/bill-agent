using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BillAgent.Worker.Data;

// EF Core entities.
//
// IMPORTANT: these are mapped to a HAND-WRITTEN schema (db/migrations/0001_init.sql).
// We do NOT use EF Core migrations. The SQL file is authoritative.
//
// Why mirror by hand? For a 3-table thesis demo, the cognitive overhead of
// `dotnet ef migrations add ...` + the snapshot files isn't worth it. The SQL
// is short enough to read aloud during the defense.
//
// Column attributes here exist only to nail down column names (Postgres convention
// is snake_case, .NET convention is PascalCase) and to be explicit about types
// for JSONB columns that need a converter (configured in BillAgentDbContext).

[Table("email_log")]
public class EmailLogEntry
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("gmail_message_id")]
    [Required]
    public string GmailMessageId { get; set; } = default!;

    [Column("subject")]
    public string? Subject { get; set; }

    [Column("sender")]
    public string? Sender { get; set; }

    [Column("kind")]
    [Required]
    public string Kind { get; set; } = default!;

    /// <summary>Full BillExtraction record serialized as JSON. Used for audit/replay.</summary>
    [Column("raw_extraction", TypeName = "jsonb")]
    [Required]
    public string RawExtractionJson { get; set; } = default!;

    [Column("processed_at")]
    public DateTimeOffset ProcessedAt { get; set; }
}

[Table("bills")]
public class Bill
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("gmail_message_id")]
    [Required]
    public string GmailMessageId { get; set; } = default!;

    [Column("vendor")]
    [Required]
    public string Vendor { get; set; } = default!;

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("currency")]
    [Required]
    public string Currency { get; set; } = default!;

    [Column("due_date")]
    public DateOnly? DueDate { get; set; }

    [Column("period")]
    public string? Period { get; set; }

    [Column("reference")]
    public string? Reference { get; set; }

    /// <summary>
    /// JSONB array of additional identifiers (bank txn codes, customer numbers, etc.).
    /// Stored as a JSON string in the DB. EF converter handles string[] ⇄ jsonb.
    /// </summary>
    [Column("related_references", TypeName = "jsonb")]
    public string[] RelatedReferences { get; set; } = Array.Empty<string>();

    [Column("confidence")]
    public double Confidence { get; set; }

    [Column("reasoning")]
    public string? Reasoning { get; set; }

    [Column("status")]
    [Required]
    public string Status { get; set; } = BillStatus.Pending;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("payments")]
public class Payment
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("gmail_message_id")]
    [Required]
    public string GmailMessageId { get; set; } = default!;

    [Column("vendor")]
    public string? Vendor { get; set; }

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("currency")]
    [Required]
    public string Currency { get; set; } = default!;

    [Column("paid_date")]
    public DateOnly? PaidDate { get; set; }

    [Column("reference")]
    public string? Reference { get; set; }

    [Column("related_references", TypeName = "jsonb")]
    public string[] RelatedReferences { get; set; } = Array.Empty<string>();

    [Column("confidence")]
    public double Confidence { get; set; }

    [Column("reasoning")]
    public string? Reasoning { get; set; }

    /// <summary>Set by Agent B (Day 7) once reconciliation finds the matching bill.</summary>
    [Column("matched_bill_id")]
    public Guid? MatchedBillId { get; set; }

    /// <summary>
    /// Day 10 (post-build): which outcome was last pushed to Telegram for this payment.
    /// Stored as the string form of ReconcilerStatus ("Matched"/"Ambiguous"/"Unmatched").
    /// The notifier short-circuits when the about-to-send status equals this; a transition
    /// to a different outcome (e.g. Ambiguous → Matched once an invoice arrives) still fires.
    /// </summary>
    [Column("last_notified_outcome")]
    public string? LastNotifiedOutcome { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
