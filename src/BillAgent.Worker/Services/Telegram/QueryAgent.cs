using System.Text;
using System.Text.Json;
using BillAgent.Worker.Data;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace BillAgent.Worker.Services.Telegram;

#pragma warning disable SKEXP0070
#pragma warning disable SKEXP0001

/// <summary>
/// Agent C — the Query agent. Same shape as Agent B (the Reconciler):
///   - one Kernel built per chat turn
///   - a fresh DbContext scope per chat turn
///   - QueryTools registered as a kernel plugin
///   - chat history threaded in so follow-up questions ("and last year?") work
///
/// What makes this the second piece of agentic evidence in the thesis:
/// the agent decides WHICH of five read-only tools answers a natural-language
/// question, in what order, with what filters. A scripted FAQ-style bot would
/// have to enumerate every question shape; Agent C composes tools.
///
/// Per-chat history is held by the caller (TelegramBotHost) in a dictionary
/// keyed by chat_id. We accept a history and a new user message; we return the
/// model's textual reply (after any tool calls have run via SK auto-calling).
/// </summary>
public class QueryAgent
{
    private const string ModelId = "gemini-2.5-flash";

    private readonly ILogger<QueryAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly string _apiKey;

    public QueryAgent(
        ILogger<QueryAgent> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        IConfiguration config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _services = services;
        _apiKey = config["GEMINI_API_KEY"]
            ?? throw new InvalidOperationException("GEMINI_API_KEY missing.");
    }

    /// <summary>
    /// Run one chat turn. `history` is mutated in place: the user message and
    /// the assistant's reply are appended so the caller can re-pass the same
    /// history on the next turn (this is how follow-up questions work).
    /// Returns the assistant's textual reply for sending to Telegram.
    /// </summary>
    public async Task<string> AskAsync(ChatHistory history, string userMessage, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillAgentDbContext>();
        var tools = new QueryTools(db, _loggerFactory.CreateLogger<QueryTools>());

        var kernel = Kernel.CreateBuilder()
            .AddGoogleAIGeminiChatCompletion(ModelId, _apiKey)
            .Build();
        kernel.Plugins.AddFromObject(tools, pluginName: "query");

        // Seed system prompt on first turn only — keep the history compact for follow-ups.
        // The date line is injected at runtime because LLMs have no clock: without it
        // the model guesses the year for "this year" questions and silently queries the
        // wrong period (it was guessing 2024/2025 and returning 0 for 2026 data).
        if (!history.Any(m => m.Role == AuthorRole.System))
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var dated = $"TODAY'S DATE is {today:yyyy-MM-dd} (year {today:yyyy}). " +
                        $"When the user says 'this year' use {today:yyyy}; 'last year' is {today.Year - 1}. " +
                        "Never guess the current year.\n\n" + SystemPrompt;
            history.AddSystemMessage(dated);
        }

        // Persist the REAL user turn. The persisted `history` only ever accumulates
        // clean text turns (system / user / assistant) — never FunctionCallContent or
        // FunctionResultContent — so follow-up turns can never resend a stale tool
        // message. That's why the old SanitizeForNextTurn scrubber is gone.
        history.AddUserMessage(userMessage);

        var chat = kernel.GetRequiredService<IChatCompletionService>();

        string replyText;
        try
        {
            replyText = await RunToolLoopAsync(history, kernel, chat, ct);
        }
        catch (HttpOperationException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("QueryAgent: rate limit exhausted after retries.");
            replyText = "⏳ I'm being rate-limited by the model. Give me about a minute and ask again.";
        }
        catch (HttpOperationException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogError(
                "QueryAgent: Gemini 400 Bad Request. Response body: {Body}",
                ex.ResponseContent ?? "(empty)");
            replyText = "Sorry — I couldn't answer that one. Try again in a moment.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryAgent chat failed.");
            replyText = "Sorry — I couldn't answer that one. Try again in a moment.";
        }

        // Persist the REAL assistant turn.
        history.AddAssistantMessage(replyText);
        return replyText;
    }

    private const int MaxToolRounds = 5;

    /// <summary>
    /// Manual tool-calling loop — the workaround for the SK Gemini connector bug
    /// (issue: SK serializes tool results with role="function", which the Gemini
    /// REST API rejects with 400 "Role 'function' is not supported"). We disable
    /// SK auto-invocation, pull the FunctionCallContent out of the model's reply,
    /// execute each call ourselves, and feed the results back as a plain USER text
    /// message. Gemini therefore only ever sees USER/MODEL roles.
    ///
    /// The loop runs on a THROWAWAY copy of the history so the synthetic
    /// "tool results" user messages never leak into the persisted history.
    /// </summary>
    private async Task<string> RunToolLoopAsync(
        ChatHistory persisted, Kernel kernel, IChatCompletionService chat, CancellationToken ct)
    {
        // Working copy: persisted turns + our synthetic tool-result messages.
        var working = new ChatHistory();
        foreach (var m in persisted) working.Add(m);

        // autoInvoke:false → the connector advertises the tools and returns the
        // model's chosen FunctionCallContent items WITHOUT trying to invoke them
        // and round-trip the (broken) function-role result itself.
        var settings = new GeminiPromptExecutionSettings
        {
            Temperature = 0.2,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
        };

        int emptyNudges = 0;
        for (int round = 0; round < MaxToolRounds; round++)
        {
            var reply = await CallWithRetryAsync(
                () => chat.GetChatMessageContentAsync(working, settings, kernel, ct),
                ct);

            var calls = FunctionCallContent.GetFunctionCalls(reply).ToList();
            if (calls.Count == 0)
            {
                // Non-empty text → this is the final natural-language answer.
                if (!string.IsNullOrWhiteSpace(reply.Content))
                    return reply.Content;

                // Empty text AND no tool call. Gemini intermittently returns a
                // genuinely empty candidate — most often on short context-dependent
                // turns like "yes please" where it relies entirely on the prior
                // assistant question. Rather than surface a cryptic "(no reply)",
                // nudge once and let it try again. The nudge is a USER message
                // (valid Gemini role) restating that an answer is expected.
                if (emptyNudges < 1)
                {
                    emptyNudges++;
                    _logger.LogWarning(
                        "QueryAgent: empty reply with no tool call (round {Round}). Nudging once.", round);
                    working.AddUserMessage(
                        "(You returned nothing. Answer my previous message now — call a tool if you " +
                        "need data, otherwise reply in plain text.)");
                    continue;
                }

                // Still empty after a nudge — give the user something actionable
                // instead of "(no reply)".
                _logger.LogWarning("QueryAgent: still empty after nudge. Returning fallback.");
                return "Sorry — I didn't catch that. Could you rephrase, or tell me which bills you mean?";
            }

            // Execute every requested call and assemble a single results message.
            var sb = new StringBuilder();
            sb.AppendLine(
                "Results of the data lookups you requested. Use ONLY these to answer the " +
                "user's question, in plain text per your instructions:");
            foreach (var call in calls)
            {
                try
                {
                    var result = await call.InvokeAsync(kernel, ct);
                    var json = JsonSerializer.Serialize(result.Result, JsonOpts);
                    sb.AppendLine($"- {call.FunctionName}({FormatArgs(call.Arguments)}) => {json}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "QueryAgent: tool {Fn} threw.", call.FunctionName);
                    sb.AppendLine($"- {call.FunctionName}({FormatArgs(call.Arguments)}) => ERROR: {ex.Message}");
                }
            }

            // Feed results back as USER text (valid Gemini role) and let the model
            // either compose the final answer or request more tools next round.
            working.AddUserMessage(sb.ToString());
        }

        // Safety valve: model kept asking for tools past the cap. Make one final
        // request that forbids tools so we get a text answer instead of looping.
        var finalSettings = new GeminiPromptExecutionSettings
        {
            Temperature = 0.2,
            FunctionChoiceBehavior = FunctionChoiceBehavior.None(),
        };
        var finalReply = await CallWithRetryAsync(
            () => chat.GetChatMessageContentAsync(working, finalSettings, kernel, ct),
            ct);
        return string.IsNullOrWhiteSpace(finalReply.Content)
            ? "Sorry — I couldn't compose an answer from the data. Try rephrasing."
            : finalReply.Content;
    }

    private static string FormatArgs(KernelArguments? args)
    {
        if (args is null || args.Count == 0) return "";
        return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"));
    }

    // UnsafeRelaxedJsonEscaping so Cyrillic vendor names (Телекабел) are fed to the
    // model as readable text rather than \uXXXX escapes that could confuse it.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private async Task<T> CallWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        // 10/30/60s = ~100s total. Gemini free tier is ~10 RPM, so a 60s tail
        // gives the bucket time to refill. Shorter schedules race the quota
        // window and tend to exhaust without ever waiting long enough to succeed.
        var delays = new[] { 10, 30, 60 };
        for (int attempt = 0; attempt <= delays.Length; attempt++)
        {
            try { return await action(); }
            catch (HttpOperationException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                      && attempt < delays.Length)
            {
                _logger.LogWarning("Gemini rate-limited (429). Backing off {Delay}s.", delays[attempt]);
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct);
            }
        }
        return await action();
    }

    // ── prompt ───────────────────────────────────────────────────────────────
    //
    // Design choices, captured next to the prompt as with Agent B:
    //
    // 1. The agent is told its tool surface is READ-ONLY. This is both true
    //    (no [KernelFunction] in QueryTools mutates anything) and a useful
    //    instruction — it discourages the agent from inventing actions like
    //    "I will mark this paid for you", which would not work and would
    //    confuse the user.
    //
    // 2. Currency hints. The thesis dataset is MKD-denominated; we tell the
    //    agent so it doesn't invent currency conversions or assume USD.
    //
    // 3. Output format: short, plain-text, no markdown. Telegram renders some
    //    markdown but our messages go via ParseMode.None for simplicity and
    //    safety (no parse-error replies from the Bot API).
    //
    // 4. Two worked examples — one trivial ("any unpaid?"), one composed
    //    ("how much have I paid Telekabel this year"). These show the agent
    //    how to pick a tool and how to phrase the answer.
    private const string SystemPrompt = """
        You are Agent C — the Query agent — in a utility-bill tracking system.
        Your job is to answer the user's questions about their bills using the
        tools available. You are READ-ONLY: you cannot mark bills paid, change
        statuses, or modify anything. You can only RETRIEVE.

        TOOLS YOU CAN CALL:
        - find_vendors(query?) — resolve a loose provider name to the EXACT stored
          vendor string(s). CALL THIS FIRST whenever the user names a provider.
        - list_bills(status?, vendor?, period?, limit) — list bills by filter
        - bill_status(vendor, period) — single bill lookup
        - monthly_summary(year, month) — totals for one month
        - unpaid_count() — how many bills are pending right now
        - yearly_total(year, vendor?) — total paid in a year, optionally per vendor

        VENDOR RESOLUTION (important):
        The user types loose names ('Telekabel', 'Телеком', 'kolektorski'). The
        database stores exact names with prefixes ('ЈП') and city suffixes
        (' - Скопје'), often in Cyrillic. A guessed name will NOT match and you
        will silently get 0 results. So whenever the user names a provider:
          1. Call find_vendors with a short core token first.
          2. Take the exact string it returns.
          3. Pass THAT verbatim as the vendor argument to the next tool.
        If find_vendors returns several candidates, pick the closest; if unsure,
        ask the user which one they meant.

        DATA NOTES:
        - All amounts are in MKD (Macedonian denar) unless a row says otherwise.
        - Periods are 'YYYY-MM' strings (e.g. '2026-04').
        - Vendor names in the database often have prefixes like 'ЈП' or city
          suffixes like ' - Скопје'. When the user asks about 'Telekabel' or
          'Колекторски', pass the core token as the vendor filter — not the
          full string.

        ANSWER STYLE:
        - Short. Telegram messages are read on a phone. Two or three lines is
          ideal. A long table is bad.
        - Plain text. NO markdown. No bold, no code blocks, no emoji-heavy
          tables. A single leading emoji per message is fine.
        - State the number first, the qualifiers second. "1,406 MKD paid to
          Телекабел for 2026-04" is good. "After reviewing all bills, I found
          that you paid..." is bad.
        - If a tool returns nothing relevant, say so plainly. Do not invent.

        EXAMPLES:

        User: any unpaid?
        → call unpaid_count()
        → if result is 0: "Nothing unpaid right now ✅"
        → if result is N: "You have N pending bill(s). Want me to list them?"

        User: how much have I paid Telekabel this year?
        → call find_vendors(query="Telekabel") → returns ["Телекабел"]
        → call yearly_total(year=<TODAY'S YEAR>, vendor="Телекабел")
        → "3,489 MKD paid to Телекабел across 2 bills in 2026."

        User: show me April
        → call monthly_summary(year=<current year>, month=4)
        → "April 2026: 1,406 MKD paid (1 bill), 0 MKD pending."

        Always answer the user in the language they wrote in (Macedonian or
        English). Match their tone — short messages get short replies.
        """;
}

#pragma warning restore SKEXP0001
#pragma warning restore SKEXP0070
