using BillAgent.Worker.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;

namespace BillAgent.Worker.Services;

/// <summary>
/// Appends bills to a Google Sheet — the human-facing view of the system.
///
/// Design role: Postgres is the source of truth. The sheet is a derived,
/// human-readable projection of `bills` that the user (and committee) can
/// open in a browser. Day 5 only handles append-on-new-invoice; Day 8 will
/// add upsert-on-status-change once Agent B starts marking bills paid.
///
/// OAuth model: same Google Cloud project / client as GmailReader, but a
/// separate token store path so the Sheets scope gets its own refresh token.
/// First run triggers a browser consent screen for the spreadsheets scope.
///
/// Configuration (env vars / .env at repo root):
///   BILLAGENT_SHEET_ID  — the spreadsheet id from the URL
///                          (https://docs.google.com/spreadsheets/d/<THIS_PART>/edit)
///   BILLAGENT_SHEET_TAB — optional; defaults to "Bills"
/// </summary>
public class SheetsWriter
{
    // Sheets scope — write access (read+write). Spreadsheets.Spreadsheets is the
    // full-access scope; we need WRITE because we both read A1 (header check) and append.
    private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };

    private const string ApplicationName = "bill-agent";
    private const string DefaultTab = "Bills";

    // Reuses the SAME OAuth client file as Gmail (same Google Cloud project).
    // Separate token store path so Gmail's readonly token and Sheets' write token
    // don't collide — each gets its own refresh token scoped to what it needs.
    private static readonly string CredentialsFile = ResolveFromRepoRoot("secrets/google_oauth_client.json");
    private static readonly string TokenStorePath  = ResolveFromRepoRoot("secrets/token_store_sheets");

    private static string ResolveFromRepoRoot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, relative);
    }

    // The header row written into A1:J1 on first run. Order matters — every
    // AppendBillAsync call must produce values in the same order.
    private static readonly IList<object> HeaderRow = new List<object>
    {
        "Created",        // A — when the row landed (ISO 8601)
        "Vendor",         // B
        "Period",         // C — "2026-04" etc.
        "Amount",         // D
        "Currency",       // E — MKD
        "Due Date",       // F — ISO date
        "Status",         // G — pending | paid | needs_review
        "Reference",      // H — primary invoice number
        "Confidence",     // I — 0..1
        "Gmail Msg Id",   // J — traceability back to email_log
    };

    private readonly ILogger<SheetsWriter> _logger;
    private SheetsService? _service;
    private readonly string _spreadsheetId;
    private readonly string _tab;

    public SheetsWriter(ILogger<SheetsWriter> logger)
    {
        _logger = logger;
        _spreadsheetId = Environment.GetEnvironmentVariable("BILLAGENT_SHEET_ID")
            ?? throw new InvalidOperationException(
                "BILLAGENT_SHEET_ID is not set. Create a Google Sheet, copy its id from the URL, " +
                "and add `BILLAGENT_SHEET_ID=...` to .env at repo root.");
        _tab = Environment.GetEnvironmentVariable("BILLAGENT_SHEET_TAB") ?? DefaultTab;
    }

    /// <summary>
    /// OAuth handshake + verify the spreadsheet is reachable + ensure header row.
    /// First call opens a browser for the spreadsheets-scope consent screen.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_service is not null) return;

        if (!File.Exists(CredentialsFile))
            throw new FileNotFoundException(
                $"OAuth client file missing: {Path.GetFullPath(CredentialsFile)}");

        await using var stream = new FileStream(CredentialsFile, FileMode.Open, FileAccess.Read);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            user: "default",
            taskCancellationToken: ct,
            dataStore: new FileDataStore(TokenStorePath, fullPath: true));

        _service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });

        // Confirm the spreadsheet exists + we can see the target tab. Fail fast
        // here with a useful message rather than letting an append throw later.
        var meta = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync(ct);
        var hasTab = meta.Sheets.Any(s => string.Equals(s.Properties.Title, _tab, StringComparison.Ordinal));
        if (!hasTab)
            throw new InvalidOperationException(
                $"Sheet tab '{_tab}' not found in spreadsheet '{meta.Properties.Title}'. " +
                $"Either rename the default tab to '{_tab}' or set BILLAGENT_SHEET_TAB to an existing tab name.");

        _logger.LogInformation("Sheets connected: '{Title}' / tab '{Tab}'", meta.Properties.Title, _tab);

        await EnsureHeaderAsync(ct);
    }

    /// <summary>
    /// Writes the header row to A1:J1 if A1 is empty.
    /// Idempotent — safe to call on every worker startup.
    /// </summary>
    private async Task EnsureHeaderAsync(CancellationToken ct)
    {
        if (_service is null) throw new InvalidOperationException("Call InitializeAsync first.");

        // Probe A1 — if it has any value we assume the header is already there.
        var probe = await _service.Spreadsheets.Values
            .Get(_spreadsheetId, $"{_tab}!A1:A1").ExecuteAsync(ct);

        var a1HasValue = probe.Values is { Count: > 0 } && probe.Values[0] is { Count: > 0 }
            && probe.Values[0][0] is string s && !string.IsNullOrWhiteSpace(s);

        if (a1HasValue)
        {
            _logger.LogDebug("Header row already present in '{Tab}'.", _tab);
            return;
        }

        var update = new ValueRange
        {
            Values = new List<IList<object>> { HeaderRow }
        };
        var req = _service.Spreadsheets.Values.Update(update, _spreadsheetId, $"{_tab}!A1:J1");
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await req.ExecuteAsync(ct);
        _logger.LogInformation("Wrote header row to '{Tab}!A1:J1'.", _tab);
    }

    /// <summary>
    /// Appends one bill row at the bottom of the tab.
    /// USER_ENTERED so Sheets parses dates/numbers natively (the date column
    /// becomes a real date Sheets can sort by, not a string).
    /// </summary>
    public async Task AppendBillAsync(Bill bill, CancellationToken ct)
    {
        if (_service is null) throw new InvalidOperationException("Call InitializeAsync first.");

        var row = new List<object>
        {
            bill.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            bill.Vendor,
            bill.Period ?? "",
            bill.Amount,
            bill.Currency,
            bill.DueDate?.ToString("yyyy-MM-dd") ?? "",
            bill.Status,
            bill.Reference ?? "",
            bill.Confidence,
            bill.GmailMessageId,
        };

        var body = new ValueRange { Values = new List<IList<object>> { row } };
        var req = _service.Spreadsheets.Values.Append(body, _spreadsheetId, $"{_tab}!A:J");
        // USER_ENTERED → Sheets interprets dates/numbers like a user typing them in,
        // so the Amount and Date columns get the right cell types (sortable, formattable).
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        // INSERT_ROWS keeps any below-table content intact (in case the user adds
        // summary formulas underneath, common for "yearly stats" rows).
        req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

        await req.ExecuteAsync(ct);
        _logger.LogInformation("Appended bill {Vendor} {Amount} {Currency} (msg={Id}) to sheet.",
            bill.Vendor, bill.Amount, bill.Currency, bill.GmailMessageId);
    }

    /// <summary>
    /// Updates the Status cell (column G) for the row whose Gmail Msg Id (column J) matches.
    /// Called by ReconcilerAgent after a successful mark_bill_paid or flag_bill_needs_review.
    ///
    /// Implementation: read column J, find the row index, write column G of that row.
    /// Two API calls per update — acceptable here because reconciliation throughput is low
    /// (one call per matched payment, a few per sweep). For larger volumes the right move
    /// would be batched: read J once, accumulate updates, write with BatchUpdate.
    ///
    /// Returns true if the row was found and updated, false otherwise (missing row is
    /// logged but does NOT throw — Postgres is the source of truth; the sheet can be
    /// re-synced manually if it falls out of step).
    /// </summary>
    public async Task<bool> UpdateBillStatusAsync(string gmailMessageId, string newStatus, CancellationToken ct)
    {
        if (_service is null) throw new InvalidOperationException("Call InitializeAsync first.");

        // Read column J (Gmail Msg Id) from row 2 onward (row 1 is the header).
        var jColumn = await _service.Spreadsheets.Values
            .Get(_spreadsheetId, $"{_tab}!J2:J").ExecuteAsync(ct);

        if (jColumn.Values is null)
        {
            _logger.LogWarning("Sheet '{Tab}' has no data rows; cannot update status for msg={Id}.", _tab, gmailMessageId);
            return false;
        }

        // Find the 0-based offset within J2:J; add 2 (row 1 is header, J is 0-based) to get the sheet row number.
        int rowIndex = -1;
        for (int i = 0; i < jColumn.Values.Count; i++)
        {
            var cell = jColumn.Values[i];
            if (cell.Count > 0 && string.Equals(cell[0]?.ToString(), gmailMessageId, StringComparison.Ordinal))
            {
                rowIndex = i + 2;  // +2: row 1 is header, plus the +1 to convert 0-based to 1-based.
                break;
            }
        }

        if (rowIndex < 0)
        {
            _logger.LogWarning("No sheet row found for gmail_message_id={Id}; status update skipped.", gmailMessageId);
            return false;
        }

        // Status lives in column G. Update just that one cell.
        var update = new ValueRange { Values = new List<IList<object>> { new List<object> { newStatus } } };
        var req = _service.Spreadsheets.Values.Update(update, _spreadsheetId, $"{_tab}!G{rowIndex}");
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await req.ExecuteAsync(ct);

        _logger.LogInformation("Updated sheet row {Row} status → '{Status}' (msg={Id}).", rowIndex, newStatus, gmailMessageId);
        return true;
    }
}
