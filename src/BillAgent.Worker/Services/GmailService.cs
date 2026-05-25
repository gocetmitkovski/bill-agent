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

    /// <summary>
    /// Walks a message's MIME parts and yields every PDF attachment as (filename, bytes).
    /// Gmail stores attachments separately from the message body; we have to fetch each
    /// attachment by its attachmentId in a second call.
    /// </summary>
    public async Task<IReadOnlyList<(string FileName, byte[] Bytes)>> GetPdfAttachmentsAsync(
        Message message, CancellationToken ct)
    {
        if (_service is null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        var results = new List<(string, byte[])>();
        if (message.Payload is null) return results;

        // MIME parts can be nested arbitrarily (multipart/mixed > multipart/alternative > part).
        // Flatten the tree with a small recursive walker.
        foreach (var part in EnumerateParts(message.Payload))
        {
            var isPdf =
                string.Equals(part.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                (part.Filename ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            if (!isPdf) continue;
            if (string.IsNullOrEmpty(part.Body?.AttachmentId)) continue;

            var att = await _service.Users.Messages.Attachments
                .Get("me", message.Id, part.Body.AttachmentId)
                .ExecuteAsync(ct);

            // Gmail returns attachment data base64url-encoded. .NET's Convert.FromBase64String
            // doesn't accept the URL-safe variant, so we normalize first.
            var bytes = Base64UrlDecode(att.Data);
            results.Add((part.Filename ?? "attachment.pdf", bytes));
        }

        return results;
    }

    private static IEnumerable<MessagePart> EnumerateParts(MessagePart root)
    {
        yield return root;
        if (root.Parts is null) yield break;
        foreach (var child in root.Parts)
            foreach (var nested in EnumerateParts(child))
                yield return nested;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        // Gmail uses URL-safe base64: '-' instead of '+', '_' instead of '/', no padding.
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Pulls the headers and plaintext body out of a Gmail message into a flat shape
    /// that's easy to feed to an LLM. We prefer text/plain; if the email is HTML-only
    /// we return the HTML and let the next layer strip tags (or the LLM cope — Gemini does).
    /// </summary>
    public static EmailContent ExtractContent(Message message)
    {
        var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();
        string H(string name) => headers.FirstOrDefault(
            h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        string? plain = null;
        string? html = null;

        if (message.Payload is not null)
        {
            foreach (var part in EnumerateParts(message.Payload))
            {
                if (string.IsNullOrEmpty(part.Body?.Data)) continue;
                var text = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(part.Body.Data));
                if (part.MimeType == "text/plain" && plain is null) plain = text;
                else if (part.MimeType == "text/html" && html is null) html = text;
            }
        }

        return new EmailContent(
            Id: message.Id,
            Subject: H("Subject"),
            From: H("From"),
            Date: H("Date"),
            Snippet: message.Snippet ?? "",
            BodyPlain: plain,
            BodyHtml: html);
    }
}

public record EmailContent(
    string Id,
    string Subject,
    string From,
    string Date,
    string Snippet,
    string? BodyPlain,
    string? BodyHtml);
