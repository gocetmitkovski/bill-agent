using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace BillAgent.Worker.Services.Telegram;

/// <summary>
/// Push side of Day 10 — outbound only. Sends short notifications to the
/// whitelist's first chat_id when state changes:
///   - Agent A ingests a new invoice
///   - Agent B reconciles (matched / needs_review / unmatched)
///
/// This class is the analogue of SheetsWriter: a thin projection of state
/// changes onto a user-facing surface. It does NOT hold any agent reasoning —
/// the message text is composed by the caller (Worker / ReconcilerAgent) and
/// passed in verbatim. The agent does not know Telegram exists.
///
/// Failure mode: every send is wrapped in try/catch. A Telegram outage must
/// NEVER fail the ingest or reconciliation pipeline; Postgres is the source
/// of truth and the user can always re-derive state from the database.
/// </summary>
public class TelegramNotifier
{
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly TelegramWhitelist _whitelist;
    private readonly ITelegramBotClient? _client;
    private readonly bool _enabled;

    public TelegramNotifier(
        ILogger<TelegramNotifier> logger,
        TelegramWhitelist whitelist,
        IConfiguration config)
    {
        _logger = logger;
        _whitelist = whitelist;

        var token = config["BILLAGENT_TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            // Missing token is a soft failure — the rest of the system still runs.
            // Used during local dev when the user hasn't created the bot yet.
            _logger.LogWarning("BILLAGENT_TELEGRAM_BOT_TOKEN not set — Telegram notifications disabled.");
            _enabled = false;
            return;
        }

        _client = new TelegramBotClient(token);
        _enabled = true;
    }

    /// <summary>Shared client instance so the BotHost can long-poll without re-instantiating.</summary>
    public ITelegramBotClient? Client => _client;

    public bool IsEnabled => _enabled;

    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        if (!_enabled || _client is null)
            return;

        if (_whitelist.PushDestination is not long chatId)
        {
            _logger.LogDebug("Telegram push skipped: whitelist empty, no destination configured.");
            return;
        }

        try
        {
            await _client.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.None,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Swallow — Telegram is a notifier, not a source of truth.
            _logger.LogError(ex, "Telegram send failed (chat {ChatId}). Continuing.", chatId);
        }
    }
}
