using System.Collections.Concurrent;
using Microsoft.SemanticKernel.ChatCompletion;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BillAgent.Worker.Services.Telegram;

#pragma warning disable SKEXP0001

/// <summary>
/// Long-polling host for inbound Telegram messages. Runs alongside the main
/// Worker as a separate BackgroundService. Routes whitelisted inbound text to
/// Agent C; rejects everything else with a polite "you're not allowed" reply
/// that also exposes the sender's chat_id (so the operator can whitelist on
/// first contact without needing a third-party id-discovery bot).
///
/// Per-chat history is held in-process. It is lost on restart, which is fine —
/// Postgres is canonical, the history is conversational glue. A persistent
/// design is deferred (thesis is not about conversation continuity).
///
/// Why a separate BackgroundService and not interleaved with Worker:
/// the polling cadences are completely different. Worker ticks once per day
/// (or once per N seconds in demo mode). The bot has to be live-responsive —
/// long-polling effectively means "blocked on Telegram's server until a message
/// arrives or timeout." Mixing the two loops would either delay tick cadence
/// or starve user replies. Two hosted services, one process, two responsibilities.
/// </summary>
public class TelegramBotHost : BackgroundService
{
    /// <summary>Cap on per-chat history length (message count, system prompt excluded).</summary>
    private const int MaxHistoryMessages = 20;

    private readonly ILogger<TelegramBotHost> _logger;
    private readonly TelegramNotifier _notifier;
    private readonly TelegramWhitelist _whitelist;
    private readonly QueryAgent _agent;

    private readonly ConcurrentDictionary<long, ChatHistory> _histories = new();

    public TelegramBotHost(
        ILogger<TelegramBotHost> logger,
        TelegramNotifier notifier,
        TelegramWhitelist whitelist,
        QueryAgent agent)
    {
        _logger = logger;
        _notifier = notifier;
        _whitelist = whitelist;
        _agent = agent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_notifier.IsEnabled || _notifier.Client is null)
        {
            _logger.LogWarning("TelegramBotHost: notifier disabled — not starting long-polling loop.");
            return;
        }

        var client = _notifier.Client;
        var me = await client.GetMe(stoppingToken);
        _logger.LogInformation(
            "Telegram bot online as @{Username} (id={Id}). Whitelist size: {Size}. {Bootstrap}",
            me.Username, me.Id, _whitelist.All.Count,
            _whitelist.IsEmpty
                ? "BOOTSTRAP MODE — first inbound message will print its chat_id; copy it into BILLAGENT_TELEGRAM_ALLOWED_CHAT_IDS and restart."
                : "");

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
            DropPendingUpdates = true,
        };

        await client.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is not { } msg || msg.Text is null)
            return;

        var chatId = msg.Chat.Id;
        var text = msg.Text.Trim();

        // ── Whitelist gate ──────────────────────────────────────────────────
        if (_whitelist.IsEmpty)
        {
            // Bootstrap: log loudly so the operator can copy the id.
            _logger.LogWarning(
                "[BOOTSTRAP] Inbound message from chat_id={ChatId} (user='{User}'): {Text}",
                chatId, msg.From?.Username ?? msg.From?.FirstName ?? "?", text);
            await SafeReplyAsync(client, chatId,
                $"This bot is private. Your chat_id is {chatId}. Ask the operator to add it to BILLAGENT_TELEGRAM_ALLOWED_CHAT_IDS.",
                ct);
            return;
        }

        if (!_whitelist.IsAllowed(chatId))
        {
            _logger.LogWarning(
                "Rejected message from non-whitelisted chat_id={ChatId} (user='{User}'): {Text}",
                chatId, msg.From?.Username ?? msg.From?.FirstName ?? "?", text);
            await SafeReplyAsync(client, chatId,
                $"This bot is private. Your chat_id is {chatId}. Ask the operator to whitelist it.",
                ct);
            return;
        }

        // ── /commands ───────────────────────────────────────────────────────
        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            await SafeReplyAsync(client, chatId,
                "BillAgent is online. Ask me about your bills.\n" +
                "Examples: 'any unpaid?', 'show me April', 'how much have I paid Телекабел this year?'",
                ct);
            return;
        }
        if (text.Equals("/reset", StringComparison.OrdinalIgnoreCase))
        {
            _histories.TryRemove(chatId, out _);
            await SafeReplyAsync(client, chatId, "Conversation history cleared.", ct);
            return;
        }

        // ── Agent C ─────────────────────────────────────────────────────────
        var history = _histories.GetOrAdd(chatId, _ => new ChatHistory());

        // Show "typing..." while we work — short queries finish before the
        // status expires (~5s), longer ones get re-emitted by the agent loop.
        try
        {
            await client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        }
        catch { /* non-fatal */ }

        var reply = await _agent.AskAsync(history, text, ct);

        // Cap history growth — keep system prompt + last N messages.
        TrimHistory(history);

        await SafeReplyAsync(client, chatId, reply, ct);
    }

    private Task HandlePollingErrorAsync(
        ITelegramBotClient client, Exception exception, CancellationToken ct)
    {
        // Telegram.Bot calls this on transport errors; just log and let the
        // library reconnect. Don't throw — that would kill the receiver loop.
        _logger.LogError(exception, "Telegram polling error.");
        return Task.CompletedTask;
    }

    private async Task SafeReplyAsync(
        ITelegramBotClient client, long chatId, string text, CancellationToken ct)
    {
        try
        {
            await client.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.None,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMessage to {ChatId} failed.", chatId);
        }
    }

    private static void TrimHistory(ChatHistory history)
    {
        // Keep system prompt + last MaxHistoryMessages user/assistant messages.
        var nonSystem = history.Where(m => m.Role != AuthorRole.System).ToList();
        if (nonSystem.Count <= MaxHistoryMessages)
            return;

        var system = history.FirstOrDefault(m => m.Role == AuthorRole.System);
        history.Clear();
        if (system is not null) history.Add(system);
        foreach (var m in nonSystem.Skip(nonSystem.Count - MaxHistoryMessages))
            history.Add(m);
    }
}

#pragma warning restore SKEXP0001
