using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace BillAgent.Worker.Services;

#pragma warning disable SKEXP0070 // Google connector is experimental in Semantic Kernel — accepted, documented in DECISIONS.md.

/// <summary>
/// Agent A — Extractor.
/// Reads an EmailContent + optional PDF text and returns a structured BillExtraction.
///
/// Implementation notes:
/// - Single Semantic Kernel call to Gemini, no tool use yet (extraction is a pure transform).
/// - JSON mode via response_mime_type so we don't have to regex JSON out of free-form text.
/// - Prompt is multilingual-aware: emails arrive in Macedonian; the agent reasons across languages
///   without any hardcoded keyword lists.
/// - PDF text is included as a hint; even partially-broken Cyrillic still contains usable numbers
///   and Latin tokens. Vision fallback comes in Day 11.
/// </summary>
public class BillExtractor
{
    // Gemini 2.5 Flash: current free-tier model (2.0 Flash had its free-tier quota set to 0
    // mid-2025 after 2.5 became the default). Newer model, better multilingual quality,
    // native JSON mode. Free tier: ~15 req/min, 1500 req/day, 1M tokens/min.
    private const string ModelId = "gemini-2.5-flash";

    private readonly ILogger<BillExtractor> _logger;
    private readonly IChatCompletionService _chat;

    public BillExtractor(ILogger<BillExtractor> logger, IConfiguration config)
    {
        _logger = logger;

        var apiKey = config["GEMINI_API_KEY"]
            ?? throw new InvalidOperationException(
                "GEMINI_API_KEY missing. Add it to .env or set as env var.");

        var kernel = Kernel.CreateBuilder()
            .AddGoogleAIGeminiChatCompletion(ModelId, apiKey)
            .Build();

        _chat = kernel.GetRequiredService<IChatCompletionService>();
    }

    public async Task<BillExtraction> ExtractAsync(
        EmailContent email, string? pdfText, CancellationToken ct)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(email, pdfText));

        var settings = new GeminiPromptExecutionSettings
        {
            ResponseMimeType = "application/json",
            Temperature = 0.1, // deterministic-ish — extraction not creative writing
        };

        // Gemini free tier enforces both requests/minute AND input-tokens/minute.
        // Large PDF text can blow through the token-per-minute limit fast. Retry with
        // exponential backoff: this is the correct production behavior anyway, not a hack.
        var response = await CallWithRetryAsync(
            () => _chat.GetChatMessageContentAsync(history, settings, cancellationToken: ct),
            ct);
        var json = response.Content ?? "{}";

        _logger.LogDebug("Gemini raw response: {Json}", json);

        try
        {
            var result = JsonSerializer.Deserialize<BillExtraction>(json, JsonOpts)
                ?? throw new InvalidOperationException("Deserialization returned null");
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gemini returned invalid JSON: {Json}", json);
            // Return a low-confidence "other" rather than crash — Day 11 will route this to needs_review.
            return new BillExtraction("other", null, null, null, null, null, null, null,
                RelatedReferences: Array.Empty<string>(),
                Confidence: 0.0,
                Reasoning: $"Failed to parse model response as JSON: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Retries on 429 (rate limit) with exponential backoff: 5s, 15s, 30s.
    /// Gemini's error responses include a suggested retryDelay but Semantic Kernel
    /// doesn't surface it — we use sensible defaults instead.
    /// </summary>
    private async Task<T> CallWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var delays = new[] { 5, 15, 30 };
        for (int attempt = 0; attempt <= delays.Length; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Microsoft.SemanticKernel.HttpOperationException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                      && attempt < delays.Length)
            {
                var delay = delays[attempt];
                _logger.LogWarning("Gemini rate-limited (429). Backing off {Delay}s (attempt {Attempt}/{Max}).",
                    delay, attempt + 1, delays.Length);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
        // Last attempt — let the exception propagate.
        return await action();
    }

    private const string SystemPrompt = """
        You are an extraction agent for a utility-bill tracking system.

        Your job: read an email (subject, sender, body, optional PDF text) and produce
        ONE structured JSON object describing what the email represents.

        The strongest signal is usually the SUBJECT LINE. Macedonian utility companies
        use consistent patterns:
          - Invoices: contain words like "сметка" (account/bill), "фактура" (invoice),
            "електронска сметка", or vendor name + period.
          - Payment confirmations: contain words like "плаќање" (payment),
            "успешно плаќање" (successful payment), "уплата" (deposit), "потврда" (confirmation).
        Body and PDF text are reinforcement — use them to extract numbers and dates that
        are rarely in the subject.

        Notes about the PDF text:
          - PDFs from some Macedonian utility companies have broken Cyrillic font mappings,
            so Cyrillic body text may look like garbled symbols.
          - HOWEVER, numbers, Latin characters, dates and email addresses extract cleanly.
          - Trust numeric tokens (amounts, dates, invoice numbers). Distrust Cyrillic words
            inside the PDF — prefer the email subject/body for vendor identification.

        OUTPUT FORMAT — return exactly this JSON shape (no extra keys, no markdown fences):
        {
          "kind": "invoice" | "payment_confirmation" | "other",
          "vendor": "...",                   // utility company, normalized when possible
          "amount": 1234.56,                 // total due (invoice) or amount paid (confirmation)
          "currency": "MKD",                 // ISO 4217
          "dueDate": "2025-06-30",           // ISO 8601, null for confirmations
          "paidDate": "2025-05-28",          // ISO 8601, null for invoices
          "period": "2025-05",               // YYYY-MM if you can determine it
          "reference": "...",                // PRIMARY identifier — see "Reference selection" below
          "relatedReferences": ["...","..."],// ALL OTHER identifiers you saw — bank tx IDs, customer codes, merchant refs
          "confidence": 0.0,                 // your self-assessed confidence 0.0 – 1.0
          "reasoning": "..."                 // one sentence in English: why you classified/extracted this way
        }

        Reference selection (IMPORTANT):
        - Confirmation emails often contain TWO identifiers: the bank/processor's transaction ID
          (e.g. "NLB-WEB-...-69f8b319..."), AND the invoice number being paid (e.g. "04-2026-АГ7262-0").
        - `reference` should be the identifier MOST LIKELY to also appear on the matching invoice —
          which is the INVOICE NUMBER, not the bank transaction ID.
        - Put bank transaction codes, merchant refs, customer codes etc. in `relatedReferences`.
        - For invoices, `reference` is straightforwardly the invoice number; `relatedReferences`
          carries any account/customer/contract numbers also shown.
        - If you cannot identify which token is the invoice number, set `reference` to your best
          guess and put everything else in `relatedReferences` — lower your confidence accordingly.

        Rules:
        - If you cannot determine a field, set it to null (or [] for relatedReferences). Do NOT invent values.
        - Confidence below 0.7 means the system will flag for human review — be honest.
        - "other" is a valid kind for emails that are neither invoices nor confirmations
          (newsletters, account statements, promotions, etc.).
        - Reasoning must be ONE sentence, in English, citing the strongest signal you used.
        """;

    private static string BuildUserMessage(EmailContent email, string? pdfText)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== EMAIL ===");
        sb.AppendLine($"From: {email.From}");
        sb.AppendLine($"Date: {email.Date}");
        sb.AppendLine($"Subject: {email.Subject}");
        sb.AppendLine();
        sb.AppendLine("=== BODY ===");
        sb.AppendLine(email.BodyPlain ?? email.BodyHtml ?? "(empty)");

        if (!string.IsNullOrWhiteSpace(pdfText))
        {
            sb.AppendLine();
            sb.AppendLine("=== PDF TEXT (may have broken Cyrillic encoding — numbers/Latin still reliable) ===");
            // Truncate aggressively — Gemini context is huge but bills are short.
            sb.AppendLine(pdfText.Length > 8000 ? pdfText[..8000] + "..." : pdfText);
        }

        return sb.ToString();
    }
}

#pragma warning restore SKEXP0070
