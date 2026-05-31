namespace BillAgent.Worker.Services.Telegram;

/// <summary>
/// Parses BILLAGENT_TELEGRAM_ALLOWED_CHAT_IDS (comma-separated long ids) and
/// answers two questions:
///   - is this chat_id allowed to talk to us?
///   - what is the *default* push destination (the first id on the list)?
///
/// Why a whitelist matters: a Telegram bot is world-discoverable. Anyone who
/// types its username into Telegram can DM it. Without a whitelist, the Query
/// agent (Agent C) would happily answer "how much have I paid Телекабел this
/// year" to whoever asked. This class is the trust boundary between the bot's
/// public surface (Telegram) and the private surface (Postgres).
///
/// Empty whitelist = bootstrap mode: every inbound message is logged with its
/// chat_id and answered with a "your id is X — ask the operator to whitelist
/// you" reply. This is how the operator discovers their own chat_id on day one.
/// </summary>
public class TelegramWhitelist
{
    private readonly HashSet<long> _allowed;

    public TelegramWhitelist(IConfiguration config)
    {
        var raw = config["BILLAGENT_TELEGRAM_ALLOWED_CHAT_IDS"] ?? string.Empty;
        _allowed = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
    }

    public bool IsEmpty => _allowed.Count == 0;

    public bool IsAllowed(long chatId) => _allowed.Contains(chatId);

    /// <summary>The first id on the whitelist — used as the push destination.</summary>
    public long? PushDestination => _allowed.Count == 0 ? null : _allowed.First();

    public IReadOnlyCollection<long> All => _allowed;
}
