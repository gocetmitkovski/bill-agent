using BillAgent.Worker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace BillAgent.Worker.Services.Telegram;

/// <summary>
/// Agent C's tool surface — five read-only queries against the bills/payments tables.
///
/// Design parallel with ReconcilerToolset (Day 7): a narrow, declarative set of
/// methods over a single DbContext scope, exposed via [KernelFunction]. The
/// asymmetry is deliberate: Agent B writes, Agent C only reads. Read-only is the
/// argument the thesis can make for why a public-facing chat surface is acceptable
/// over private financial data — the worst-case action the agent can take is to
/// surface information the user already owns, never to mutate it.
///
/// Tools:
///   list_bills(status?, vendor?, period?, limit)     — list bills by filter
///   bill_status(vendor, period)                      — single-bill lookup
///   monthly_summary(year, month)                     — month aggregate
///   unpaid_count()                                   — pending count, scalar
///   yearly_total(year, vendor?)                      — year aggregate, optionally per vendor
///
/// Lifecycle: a new instance is constructed per CHAT TURN with a fresh DbContext.
/// </summary>
public class QueryTools
{
    private readonly BillAgentDbContext _db;
    private readonly ILogger _logger;

    public QueryTools(BillAgentDbContext db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    [KernelFunction("find_vendors")]
    [System.ComponentModel.Description(
        "Resolve a user's loose vendor name to the EXACT vendor strings stored in the database. " +
        "Call this FIRST whenever the user names a provider (e.g. 'Telekabel', 'Телеком', 'kolektorski') " +
        "before calling yearly_total/list_bills/bill_status — the stored names have prefixes ('ЈП') and " +
        "city suffixes (' - Скопје') and may be in Cyrillic, so a guessed name often won't match. " +
        "Pass a short core token; returns the distinct stored vendor strings that loosely match, or all " +
        "vendors if you pass null. Then use one of the returned strings verbatim as the vendor argument.")]
    public async Task<IReadOnlyList<string>> FindVendorsAsync(
        [System.ComponentModel.Description("A core token of the vendor name, any script. Pass null to list every vendor.")]
        string? query = null)
    {
        var q = _db.Bills.Select(b => b.Vendor).Distinct();

        // If the model gives us a token, try a loose ILIKE first. If that yields
        // nothing (e.g. it guessed 'Телеком' for 'Телекабел'), fall back to
        // returning ALL vendors so the model can pick the right one itself —
        // better to over-return on a small table than to dead-end at zero.
        List<string> rows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = await q.Where(v => EF.Functions.ILike(v, $"%{query}%")).ToListAsync();
            if (rows.Count == 0)
                rows = await _db.Bills.Select(b => b.Vendor).Distinct().ToListAsync();
        }
        else
        {
            rows = await q.ToListAsync();
        }

        _logger.LogInformation(
            "QueryTools.FindVendors(query='{Query}') → {Count} match(es): {Vendors}",
            query ?? "all", rows.Count, string.Join(" | ", rows));

        return rows;
    }

    [KernelFunction("list_bills")]
    [System.ComponentModel.Description(
        "List bills matching the filter. Use to answer 'what bills do I have', 'show me unpaid bills', " +
        "'last 5 bills from Telekabel'. Returns id, vendor, period, amount, currency, due_date, status. " +
        "Pass null for filters you don't want to apply. Pass limit explicitly (10 is a reasonable default, max 50).")]
    public async Task<IReadOnlyList<BillSummary>> ListBillsAsync(
        [System.ComponentModel.Description("Filter by status: 'pending', 'paid', 'needs_review'. Pass null for any status.")]
        string? status = null,
        [System.ComponentModel.Description("Substring match on vendor (case-insensitive). Use a core token. Pass null for any vendor.")]
        string? vendor = null,
        [System.ComponentModel.Description("Filter by period 'YYYY-MM'. Pass null for any period.")]
        string? period = null,
        [System.ComponentModel.Description("Max rows to return. Use 10 as a default, max 50.")]
        int limit = 10)
    {
        var capped = Math.Clamp(limit, 1, 50);
        var query = _db.Bills.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(b => b.Status == status);
        if (!string.IsNullOrWhiteSpace(vendor))
            query = query.Where(b => EF.Functions.ILike(b.Vendor, $"%{vendor}%"));
        if (!string.IsNullOrWhiteSpace(period))
            query = query.Where(b => b.Period == period);

        var rows = await query
            .OrderByDescending(b => b.CreatedAt)
            .Take(capped)
            .Select(b => new BillSummary(
                b.Id, b.Vendor, b.Period, b.Amount, b.Currency, b.DueDate, b.Status))
            .ToListAsync();

        _logger.LogInformation(
            "QueryTools.ListBills(status='{Status}', vendor~'{Vendor}', period='{Period}', limit={Limit}) → {Count} row(s)",
            status ?? "any", vendor ?? "any", period ?? "any", capped, rows.Count);

        return rows;
    }

    [KernelFunction("bill_status")]
    [System.ComponentModel.Description(
        "Look up the status of a SINGLE bill by vendor + period. Returns status (pending/paid/needs_review) " +
        "and the bill's amount/due_date, or null if no such bill exists.")]
    public async Task<BillSummary?> BillStatusAsync(
        [System.ComponentModel.Description("Vendor substring (case-insensitive, core token).")]
        string vendor,
        [System.ComponentModel.Description("Period 'YYYY-MM'.")]
        string period)
    {
        var row = await _db.Bills
            .Where(b => EF.Functions.ILike(b.Vendor, $"%{vendor}%") && b.Period == period)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new BillSummary(
                b.Id, b.Vendor, b.Period, b.Amount, b.Currency, b.DueDate, b.Status))
            .FirstOrDefaultAsync();

        _logger.LogInformation(
            "QueryTools.BillStatus(vendor~'{Vendor}', period='{Period}') → {Result}",
            vendor, period, row is null ? "not found" : row.Status);

        return row;
    }

    [KernelFunction("monthly_summary")]
    [System.ComponentModel.Description(
        "Aggregate totals for one month. Returns total_paid (sum of paid bills with that period), " +
        "total_pending (sum of pending bills), count_paid, count_pending. Use to answer " +
        "'how much did I pay in April 2026'.")]
    public async Task<MonthlySummary> MonthlySummaryAsync(
        [System.ComponentModel.Description("Calendar year, e.g. 2026.")]
        int year,
        [System.ComponentModel.Description("Month 1-12.")]
        int month)
    {
        var period = $"{year:0000}-{month:00}";
        var rows = await _db.Bills
            .Where(b => b.Period == period)
            .Select(b => new { b.Amount, b.Status, b.Currency })
            .ToListAsync();

        var paid = rows.Where(r => r.Status == BillStatus.Paid).ToList();
        var pending = rows.Where(r => r.Status == BillStatus.Pending).ToList();
        var currency = rows.Select(r => r.Currency).FirstOrDefault() ?? "MKD";

        var summary = new MonthlySummary(
            period,
            paid.Sum(r => r.Amount),
            pending.Sum(r => r.Amount),
            paid.Count,
            pending.Count,
            currency);

        _logger.LogInformation(
            "QueryTools.MonthlySummary({Period}) → paid={Paid} pending={Pending} ({Currency})",
            period, summary.TotalPaid, summary.TotalPending, currency);

        return summary;
    }

    [KernelFunction("unpaid_count")]
    [System.ComponentModel.Description(
        "Scalar: how many bills currently have status='pending'. Use to answer 'do I owe anything right now'.")]
    public async Task<int> UnpaidCountAsync()
    {
        var count = await _db.Bills.CountAsync(b => b.Status == BillStatus.Pending);
        _logger.LogInformation("QueryTools.UnpaidCount → {Count}", count);
        return count;
    }

    [KernelFunction("yearly_total")]
    [System.ComponentModel.Description(
        "Sum of amounts for PAID bills in a calendar year. Optionally narrowed to one vendor. " +
        "Use to answer 'how much have I paid Telekabel this year' or 'total bills paid in 2025'.")]
    public async Task<YearlyTotal> YearlyTotalAsync(
        [System.ComponentModel.Description("Calendar year, e.g. 2026.")]
        int year,
        [System.ComponentModel.Description("Optional vendor substring. Null = all vendors.")]
        string? vendor = null)
    {
        var prefix = $"{year:0000}-";
        var query = _db.Bills
            .Where(b => b.Status == BillStatus.Paid)
            .Where(b => b.Period != null && b.Period.StartsWith(prefix));

        if (!string.IsNullOrWhiteSpace(vendor))
            query = query.Where(b => EF.Functions.ILike(b.Vendor, $"%{vendor}%"));

        var rows = await query.Select(b => new { b.Amount, b.Currency }).ToListAsync();
        var total = rows.Sum(r => r.Amount);
        var currency = rows.Select(r => r.Currency).FirstOrDefault() ?? "MKD";

        var result = new YearlyTotal(year, vendor, total, currency, rows.Count);
        _logger.LogInformation(
            "QueryTools.YearlyTotal(year={Year}, vendor='{Vendor}') → total={Total} {Currency} across {Count} bill(s)",
            year, vendor ?? "any", total, currency, rows.Count);

        return result;
    }
}

public record BillSummary(
    Guid Id, string Vendor, string? Period, decimal Amount, string Currency,
    DateOnly? DueDate, string Status);

public record MonthlySummary(
    string Period, decimal TotalPaid, decimal TotalPending,
    int CountPaid, int CountPending, string Currency);

public record YearlyTotal(
    int Year, string? Vendor, decimal Total, string Currency, int Count);
