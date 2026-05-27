using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BillAgent.Worker.Data;

/// <summary>
/// EF Core DbContext for bill-agent.
///
/// We deliberately do NOT use EF migrations — the schema is owned by
/// db/migrations/0001_init.sql and applied by Docker's init mechanism.
/// This context only maps onto that schema and exposes LINQ for Agent B.
/// </summary>
public class BillAgentDbContext : DbContext
{
    public DbSet<EmailLogEntry> EmailLog => Set<EmailLogEntry>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<Payment> Payments => Set<Payment>();

    public BillAgentDbContext(DbContextOptions<BillAgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── string[] ⇄ jsonb converter ───────────────────────────────────────
        // We store related_references as a JSONB array in Postgres.
        // EF Core can't auto-map string[] to jsonb without help; this converter
        // serializes/deserializes via System.Text.Json.
        var stringArrayJsonbConverter = new ValueConverter<string[], string>(
            // Going TO the DB: serialize array → JSON string (Postgres reads as jsonb).
            v => JsonSerializer.Serialize(v ?? Array.Empty<string>(), (JsonSerializerOptions?)null),
            // Coming FROM the DB: deserialize JSON string → array. Tolerate nulls/empties.
            v => string.IsNullOrWhiteSpace(v)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null) ?? Array.Empty<string>());

        // EF needs a value comparer for mutable collections behind a converter,
        // otherwise change tracking can't detect content edits (e.g. Agent B appending
        // a reference on Day 7 would silently NOT be saved). The comparer below does
        // element-wise equality + a deep clone on snapshot so the change tracker sees
        // a real "before" vs "after" diff.
        var stringArrayComparer = new ValueComparer<string[]>(
            (a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
            v => v == null ? 0 : v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v == null ? Array.Empty<string>() : v.ToArray());

        modelBuilder.Entity<Bill>()
            .Property(e => e.RelatedReferences)
            .HasConversion(stringArrayJsonbConverter, stringArrayComparer);

        modelBuilder.Entity<Payment>()
            .Property(e => e.RelatedReferences)
            .HasConversion(stringArrayJsonbConverter, stringArrayComparer);

        // ── unique idempotency keys ──────────────────────────────────────────
        // Mirrored from the SQL UNIQUE constraints so EF can plan against them
        // and so any future EF-driven query sees the same uniqueness guarantee.
        modelBuilder.Entity<EmailLogEntry>()
            .HasIndex(e => e.GmailMessageId).IsUnique();
        modelBuilder.Entity<Bill>()
            .HasIndex(e => e.GmailMessageId).IsUnique();
        modelBuilder.Entity<Payment>()
            .HasIndex(e => e.GmailMessageId).IsUnique();

        // ── Let Postgres fill timestamps (DEFAULT NOW() in the SQL schema) ───
        // Without this, EF sends the CLR default DateTimeOffset (year 0001),
        // which Npgsql translates to Postgres `-infinity`. We want server-side NOW().
        // ValueGeneratedOnAdd  → EF omits the column from INSERT statements.
        // HasDefaultValueSql   → tells EF that the DB will fill it; the value is read
        //                        back into the entity after SaveChanges so the in-memory
        //                        object matches the row.
        modelBuilder.Entity<EmailLogEntry>()
            .Property(e => e.ProcessedAt)
            .HasDefaultValueSql("NOW()")
            .ValueGeneratedOnAdd();

        modelBuilder.Entity<Bill>()
            .Property(e => e.CreatedAt)
            .HasDefaultValueSql("NOW()")
            .ValueGeneratedOnAdd();
        modelBuilder.Entity<Bill>()
            .Property(e => e.UpdatedAt)
            .HasDefaultValueSql("NOW()")
            // ValueGeneratedOnAddOrUpdate → also omitted on UPDATE, lets the
            // touch_updated_at trigger do its job (Day 7 / Day 8 reconciliation).
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder.Entity<Payment>()
            .Property(e => e.CreatedAt)
            .HasDefaultValueSql("NOW()")
            .ValueGeneratedOnAdd();
    }
}
