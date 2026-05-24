using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace BillAgent.Worker.Services;

/// <summary>
/// Wraps the Google Gmail API for our bill-agent.
/// Handles OAuth handshake (first run opens a browser),
/// then provides methods to list messages by label.
///
/// Configured for: gocefikt@gmail.com
/// Label watched: utility-bills
/// </summary>
public class GmailReader
{
    // Read-only scope: we don't want to risk modifying mailbox.
    // If we ever need to mark messages read or apply labels, change to GmailService.Scope.GmailModify.
    private static readonly string[] Scopes = { GmailService.Scope.GmailReadonly };

    private const string ApplicationName = "bill-agent";
    private static readonly string CredentialsFile = ResolveFromRepoRoot("secrets/google_oauth_client.json");
    private static readonly string TokenStorePath = ResolveFromRepoRoot("secrets/token_store");

    /// <summary>
    /// Walks up from the current directory looking for the repo root (folder containing .git),
    /// then resolves the given relative path against it. Lets us reference repo-root files
    /// regardless of where 'dotnet run' was invoked from.
    /// </summary>
    private static string ResolveFromRepoRoot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, relative);
    }

    private readonly ILogger<GmailReader> _logger;
    private GmailService? _service;

    public GmailReader(ILogger<GmailReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds the GmailService. First call triggers OAuth browser flow.
    /// Subsequent calls reuse the cached refresh token.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_service is not null) return;

        if (!File.Exists(CredentialsFile))
            throw new FileNotFoundException(
                $"OAuth client file missing: {Path.GetFullPath(CredentialsFile)}. " +
                "Download it from Google Cloud Console > APIs & Services > Credentials.");

        await using var stream = new FileStream(CredentialsFile, FileMode.Open, FileAccess.Read);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            user: "default",                                 // local identifier for token store
            taskCancellationToken: ct,
            dataStore: new FileDataStore(TokenStorePath, fullPath: true));

        _service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

        // Echo the connected account so we know OAuth landed on the right inbox.
        var profile = await _service.Users.GetProfile("me").ExecuteAsync(ct);
        _logger.LogInformation("Gmail connected as: {Email} ({Total} total messages in mailbox)",
            profile.EmailAddress, profile.MessagesTotal);
    }

    /// <summary>
    /// Returns the most recent N messages that carry the given label.
    /// We only fetch IDs here — full message bodies come in a later step.
    /// </summary>
    public async Task<IReadOnlyList<Message>> ListMessagesByLabelAsync(
        string labelName, int max, CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        // Gmail API works with label IDs, not display names — look up the ID.
        var labels = await _service.Users.Labels.List("me").ExecuteAsync(ct);
        var label = labels.Labels.FirstOrDefault(
            l => string.Equals(l.Name, labelName, StringComparison.OrdinalIgnoreCase));

        if (label is null)
        {
            _logger.LogWarning("Label '{Label}' not found. Available labels: {All}",
                labelName, string.Join(", ", labels.Labels.Select(l => l.Name)));
            return Array.Empty<Message>();
        }

        var request = _service.Users.Messages.List("me");
        request.LabelIds = label.Id;
        request.MaxResults = max;
        var response = await request.ExecuteAsync(ct);

        return response.Messages?.ToList() ?? new List<Message>();
    }

    /// <summary>
    /// Fetches the full message (headers + body + attachments metadata) for one ID.
    /// We'll use this on Day 2 when we start extracting PDFs.
    /// </summary>
    public async Task<Message> GetMessageAsync(string id, CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        var req = _service.Users.Messages.Get("me", id);
        req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        return await req.ExecuteAsync(ct);
    }
}
