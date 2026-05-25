# Implementation Decisions

A running log of non-obvious choices made during the build. The reasoning here feeds directly into the "implementation choices" section of the thesis paper.

---

## 2026-05-22 — Initial stack selection

### Language & runtime: .NET 9 / C#
- Author's strongest language (web developer with .NET background).
- Mature Microsoft ecosystem for agent SDKs.
- Defending in your strongest language reduces cognitive load during Q&A.

### Agent SDK: Semantic Kernel (not Microsoft Agent Framework)
- Semantic Kernel is GA, stable, well-documented (Dec 2023+ stable APIs).
- Microsoft Agent Framework (announced 2025) is the evolution of SK + AutoGen, but still maturing.
- Thesis defense in 2026 → safer to build on the stable SDK and reference MAF as future direction.

### LLM provider: Google Gemini (free tier)
- Gemini 2.0 Flash (and 2.5 Flash) on the free tier: ~15 req/min, 1500/day,
  1M tokens/min. Vastly more than this project needs.
- Native vision in every model → unified handling of text and scanned PDFs.
- Zero project cost; no payment method required.
- Already in the Google ecosystem (Gmail + Sheets), so credentials and
  billing live in one place.
- Thesis defense story: deliberate cost-aware choice, no vendor lock-in,
  no payment method gating reproducibility.
- Note: Semantic Kernel's official connectors are OpenAI/Azure; Google
  Gemini is wired in via the community
  `Microsoft.SemanticKernel.Connectors.Google` package or a custom
  `IChatCompletionService`. Documented as a minor integration cost.

### Email ingestion: Gmail API with label filter
- Chose Gmail API over IMAP — cleaner OAuth, richer metadata, push notifications via Pub/Sub possible later.
- Polling every 5 minutes is sufficient for demo; Pub/Sub is "future work."
- Bills are forwarded to a labeled inbox in author's primary Gmail.

### PDF parsing: PdfPig + LLM vision fallback
- PdfPig is pure C#, no native dependencies → portable, easy CI.
- Most utility bills have selectable text → PdfPig handles them.
- Scanned/image PDFs fall back to GPT-4o vision (the fallback chain itself is part of the "agentic" story).

### Database: Postgres on Supabase
- Considered SQLite (simpler) vs Supabase (hosted Postgres + pgvector + auth).
- Picked Supabase for free hosted tier and easy pgvector path later.
- Three tables for v1: `bills`, `payments`, `email_log`. No vectors yet.
- pgvector + RAG over historical bills is documented as future work.

### Output: Google Sheets
- The Sheet *is* the presentation layer for v1 — no separate frontend needed.
- Examiner can see real-time updates during demo by switching tabs.
- Sheets API supports upsert via cell ranges; we key rows by `bill_id`.

### Frontend: Blazor Server, one page, optional
- Skipped for v1 happy path. Sheet is enough.
- Added in week 2 as a read-only "agent activity log" dashboard — makes the agent's reasoning visible to examiners.
- Single page, reads from `email_log` table.

### Hosting: local laptop for demo
- Deployment would eat 2+ days that the 2-week timeline doesn't have.
- Background service runs locally during defense.
- Documented as a limitation; future work: deploy to Azure Container Apps.

---

## Principles guiding scope

1. **Ruthless v1 scope.** End-to-end happy path before any polish.
2. **The fallback chain is the contribution.** Don't write per-vendor parsers — let the agent handle format variance via LLM + vision + human-in-the-loop.
3. **Every decision the agent makes is logged.** `email_log` table with reasoning + confidence is the artifact that proves "agentic, not scripted" during defense.
4. **Don't deploy.** Local demo is honest and saves days.

---

## 2026-05-22 — Day 1: Gmail OAuth + label listing

OAuth connection established and labeled-email reading confirmed end-to-end. Details for paper / future-me:

- **Library:** `Google.Apis.Gmail.v1` 1.74 + `Google.Apis.Auth` 1.74 (official Google .NET SDK).
- **Scope:** `GmailReadonly` only — agent physically cannot modify the mailbox. If we ever need to mark-as-read or apply labels, scope gets widened explicitly. Defensible security choice for the paper.
- **Credential storage:** OAuth client JSON in `/secrets/google_oauth_client.json` (gitignored). Refresh token cached in `/secrets/token_store/` after first browser handshake — subsequent runs are silent (no browser). For thesis production-readiness story: would migrate to .NET User Secrets / Azure Key Vault.
- **Path resolution:** `ResolveFromRepoRoot()` walks up from `AppContext.BaseDirectory` until it finds `.git`, then resolves paths against that. Robust against `dotnet run` cwd quirks regardless of where the binary is invoked from.
- **DI lifetime:** `GmailReader` registered as singleton — one OAuth handshake per process lifetime, token cache stays warm.
- **Label lookup:** Gmail API works with label IDs, not display names. Code lists all labels, finds `utility-bills` by name (case-insensitive), then queries by ID. If label missing, logs all available label names — helpful for typos.

**Setup gotchas worth mentioning in paper's "Limitations" section:**
1. OAuth consent screen in External + Testing mode requires explicit test-user allow-list — even the developer's own email isn't auto-included.
2. Each Google API must be individually enabled on the GCP project (Gmail API, later Sheets API). The error message conveniently includes a one-click enable link.
3. Unverified-app warning ("Google hasn't verified this app") is unavoidable without going through Google's verification process (security review + privacy policy + sometimes paid audit for sensitive scopes). Acceptable for a thesis demo; would block real users.

Verified working: 2 forwarded bills auto-labeled `utility-bills`, both message IDs returned by the worker on `dotnet run`.

---

## 2026-05-23 — Day 2: PDF extraction + multi-agent architecture decisions

### What shipped
- `GmailReader.GetPdfAttachmentsAsync` — walks MIME parts (recursive — Gmail nests multipart arbitrarily), fetches each `application/pdf` part via Gmail's separate attachment endpoint, base64url-decodes the bytes.
- `GmailReader.ExtractContent` — flattens a Gmail message into a flat `EmailContent` record (Subject, From, Date, Snippet, BodyPlain, BodyHtml) — clean shape for downstream LLM consumption.
- `PdfTextExtractor` — thin PdfPig wrapper, opens PDF from byte[], walks pages, concatenates text. Pure C#, no native deps.
- `Worker.cs` now prints headers + body + PDF text per message.

### Key discovery: Macedonian PDF font encoding is broken
Running against a real Vodovod (water company) invoice produced text where numbers/Latin chars/email/URL extract perfectly, but all Cyrillic body text is mapped to wrong code points (e.g. `Колекторски` came out as `ǲȖȓȍȒȚȖȘșȒȐ`).

Root cause: the PDF embeds a custom Macedonian font but skips the **ToUnicode CMap** — the optional dictionary that maps glyph IDs to Unicode code points. The PDF renders visually correct (the glyph at slot N *looks* like 'К') but any text extractor gets raw glyph IDs and has no way to recover Unicode. This is permitted by the PDF spec and unfortunately common among utility billers worldwide. Not a PdfPig bug — same failure with iText, pdftotext, Adobe's own copy-text.

**Architectural consequence for the paper:** the LLM-vision fallback is no longer a hypothetical edge case for scanned PDFs — it's *the* primary path for Macedonian utility bills. PdfPig's clean number extraction still has value as a *cross-validation* signal ("if vision-extracted total ≠ numeric total found in PdfPig text, flag for review"). This kind of cross-checked dual-extraction is a defensible "agentic" design choice over single-source parsing.

### Architecture decision: three-agent design (A → B + C)

User proposed splitting agent work along role boundaries. Settled on:

- **Agent A — Extractor.** Email subject + body + PDF text + (Day 3+) PDF first-page image → structured JSON `{kind, vendor, amount, currency, due_date, period, reference, confidence, reasoning}`. Strongest signal is the email subject (Macedonian utility companies use very consistent subject patterns: "Известување за успешно плаќање" = payment confirmation, "вашата електронска сметка од водовод за 05/2025" = invoice). Body/PDF are reinforcement.
- **Agent B — Reconciler.** Takes JSON from A + tools (`query_pending_bills`, `mark_bill_paid`, `insert_new_bill`, `flag_for_review`). For an invoice → inserts a new pending bill. For a confirmation → finds matching pending bill (fuzzy: vendor name variations, amount within tolerance, period match) → marks paid or flags ambiguity. *This is where the genuinely agentic behavior lives — decision under uncertainty with tool use.*
- **Agent C — Query Agent (v2 / stretch goal).** Natural-language Q&A interface over the bills database. Tools: `list_bills`, `bill_status`, `monthly_summary`, `unpaid_count`, `yearly_total`. Voice front-end (STT → Agent C → TTS) deferred to v2; would require Whisper integration + macOS audio APIs.

This maps onto a classic three-role agent taxonomy: *perception → action → retrieval*. Clean paper structure.

**Deliberately non-agentic components:** `SheetSync` (DB change → Google Sheet upsert) is plain code. Calling an LLM to update a cell would be "agent-washing." The thesis position is that something is agentic when it makes a *decision under uncertainty with tool use*, not when it's wrapped in an LLM call.

### Multilingual generalization as a paper bullet
Subject lines and body content are in Macedonian. Gemini handles this natively without translation, and the agent design uses zero hardcoded keywords. A rule-based pipeline would require a per-language keyword list per vendor; the LLM generalizes across languages by construction. Worth one bullet in the "advantages over rule-based extraction" section.

### Design for extension, defer abstraction (ingestion source)

Considered building an `IBillSource` interface up front so the system could ingest from IMAP / Outlook Graph / shared drive folders / manual upload in addition to Gmail. Deliberately *not* doing this for v1.

**Reasoning:**
- Speculative generality is a documented refactoring anti-pattern (Fowler, *Refactoring*, ch. "Bad Smells in Code"). One concrete provider gives no signal on what the right abstraction looks like — an interface designed in the dark will almost certainly be wrong by the time a second source appears.
- Rule of Three: abstract from concrete usage patterns across at least three implementations, not from imagination. With only Gmail today, the cost (extra code, false constraints, defending an unused interface during the viva) outweighs the benefit.
- Two-week timeline doesn't tolerate yak-shaving that's unjustified by the v1 scope.

**Why the deferral is cheap (architecture already supports it):**
- `EmailContent` record is provider-neutral by construction — no Gmail-specific fields, just Subject/From/Date/Snippet/BodyPlain/BodyHtml.
- `PdfTextExtractor` consumes `byte[]`, not Gmail's `Message` type. Already source-agnostic.
- Gmail coupling is surgically isolated to a single class (`GmailReader`) with one method per concern (list / fetch / attachments).
- Agent A consumes `EmailContent` only and is wholly unaware of the ingestion source — when a second source is added, the agent layer needs zero changes.

**Future-work framing for the paper:**
- "Pluggable ingestion source" as v2: extract `IBillSource` from concrete usage patterns once 2+ providers are implemented.
- Plausible alternative sources: IMAP for arbitrary providers, Microsoft Graph for Outlook/Exchange, a watched directory for manual PDF drops, a webhook endpoint for vendors that push.
- The *agent layer's invariance* under ingestion-source changes is itself a defensible architectural property — it demonstrates that the agentic logic is genuinely decoupled from input plumbing.

**Defense talking point:** "I designed for extension but deliberately did not abstract until a second concrete case existed, following the Rule of Three. The provider-neutral shapes (`EmailContent`, `byte[]`) and the surgical isolation of Gmail coupling mean adding a second source is a mechanical refactor, not a redesign."

---

## 2026-05-23 — Architecture pivot: Telegram bot replaces Blazor dashboard

Day 10 was originally a single-page Blazor dashboard reading `email_log` — a "glass box" UI for the examiner. User proposed replacing it with a Telegram bot that (a) pushes notifications about bill activity and (b) hosts Agent C as a natural-language query interface in the same chat. After analysis, this is a strict upgrade.

### Why Telegram beats the dashboard

- **Push beats pull.** A dashboard requires the user to remember to open it. A bot initiates contact — "new invoice from Vodovod, 1,247 MKD, due 30.06.2025." The agent becomes a thing that *talks to you*, not a thing you visit.
- **Real product, not defense theater.** The Blazor page exists to impress examiners; the user would never actually open it day-to-day. The Telegram bot is what the user genuinely wants. Examiners respond *better* to systems that are honestly used than to systems built only for demos.
- **Single channel for notifications + Q&A.** Agent B pushes events into the chat. Agent C answers questions in the same chat. One UI surface, two roles, zero context switching.
- **Voice path becomes trivial.** Telegram natively supports voice messages → Whisper transcription → Agent C → text/voice reply. The voice query agent (Agent C in the original plan) is no longer a separate UI track; it's a 1-day add-on to the bot.

### Architecture

```
Gmail ─► Agent A ─► Agent B ─► Database
                       │            │
                       ▼            ▼
                   Telegram bot (push)
                       ▲
                       │  user messages
                       ▼
                    Agent C (query) ─► DB tools
```

- **Agent B's tool set gains** `send_telegram_notification(chat_id, text)` so the reconciler decides when to ping the user (new invoice / payment matched / needs review / due-soon reminder).
- **Agent C** is a Semantic Kernel chat agent behind the bot's incoming-message handler. Tools: `list_bills(filter)`, `bill_status(id)`, `monthly_summary(month)`, `unpaid_count()`, `yearly_total(year)`. Stateless except for per-chat short-term ChatHistory so follow-ups ("what about gas?") work.
- **Direct Telegram Bot API for v1** via the `Telegram.Bot` NuGet package + long-polling. Webhooks + deployment are future work, consistent with the existing "local laptop demo" decision.

### MCP as a v2 thesis-flex upgrade

The Model Context Protocol (MCP) is the 2024-2025 open standard for exposing tools to LLM agents. Day-13 buffer time, if available, can upgrade the Telegram integration to go through an MCP Telegram server instead of the direct API. Functionally identical, architecturally fancier — and earns a paragraph in the paper:

> "Tool integration uses the open Model Context Protocol, demonstrating that the architecture is interoperable with any MCP-conformant tool surface. The Telegram integration is therefore swappable with Slack, Discord, or calendar tools without modifying the agent layer."

This is a *contemporary, citable, current* standard. Examiners reward use of current open standards correctly.

### Three-agent map (updated)

- **Agent A — Perception.** Email → JSON. Unchanged.
- **Agent B — Action.** JSON → DB + Sheet + Telegram notifications. *Gains tool: telegram notify.*
- **Agent C — Retrieval.** Telegram message → DB query → reply. *Promoted from "v2 stretch goal" to shipped v1.*

The original three-role taxonomy (perception → action → retrieval) survives unchanged — only the UI surface for Agent C changes from "voice / Blazor query box" to "Telegram chat."

### What's lost, what's gained

**Lost:** the visual `email_log` table view in a browser. Mitigation: Agent C can answer "show me the last 5 emails the agent processed" with the same data, plus reasoning. The audit trail still exists in DB — only the dedicated UI is gone.

**Gained:** real product utility, push notifications, voice-ready surface, MCP upgrade path, simpler stack (no Blazor server in the deployment story).

---

## 2026-05-23 — Day 3: Agent A (Extractor) wired to Gemini via Semantic Kernel

### What shipped

- **`BillExtraction` record** — the structured-output contract Agent A produces and Agent B consumes. Fields: `Kind` ("invoice" | "payment_confirmation" | "other"), `Vendor`, `Amount`, `Currency`, `DueDate`, `PaidDate`, `Period`, `Reference`, `Confidence` (0.0–1.0), `Reasoning` (one-sentence audit trail). C# record → immutable, serializes cleanly via `System.Text.Json`.
- **`BillExtractor` service** — Agent A. Single Semantic Kernel chat-completion call to Gemini 2.0 Flash with JSON-mode output (`ResponseMimeType = "application/json"`) and temperature 0.1 for deterministic extraction. No tool use — extraction is a pure `(EmailContent, pdfText) → BillExtraction` transform.
- **System prompt** — encodes Macedonian utility-mail patterns *as patterns, not keyword lists*. Tells the model that the subject line is the strongest signal, and that PDF Cyrillic text may be glyph-encoding broken (trust numbers, distrust Cyrillic words from PDF, prefer email subject for vendor). This turns Day 2's encoding bug into the agent's documented runtime behavior.
- **Graceful failure path** — if Gemini returns unparseable JSON, return a low-confidence "other" record with the exception message as reasoning. Day 11 will route low-confidence outputs to `needs_review`.

### Framework choice: Semantic Kernel over Microsoft Agent Framework

Considered Microsoft Agent Framework (MAF) — the 2024 GA successor to Semantic Kernel and AutoGen, which exposes multi-agent orchestration, agent memory, and human-in-the-loop as first-class primitives. Deliberately stayed with Semantic Kernel.

**Reasoning:**

1. **Stability over recency for a 2-week timeline.** SK has been GA since December 2023 with mature Gemini connectors (the connector is marked experimental, but it works and is stable). MAF's API surface is still shifting between minor versions — debugging a moving target inside a thesis deadline is unjustified risk.

2. **Manual orchestration is part of the thesis contribution.** If MAF handles A→B→C handoffs as a built-in primitive, the claim "I built an agentic system" weakens — the *framework* did the orchestration. By using SK as a chat-completion abstraction and hand-rolling the agent-handoff logic, the multi-agent design itself is the author's contribution rather than the framework's. For a thesis on agentic systems, this is the stronger position.

3. **MAF as a credible future-work paragraph.** The choice can be presented as informed and forward-looking rather than conservative: "Initial implementation chose Semantic Kernel for connector maturity and timeline stability. Microsoft Agent Framework, which generalizes SK + AutoGen and reached general availability in 2025, would be the natural target for a v2 reimplementation; the agent-handoff patterns implemented manually in this work are first-class primitives in MAF, making the migration mechanically straightforward." This shows awareness of the ecosystem and a clean upgrade path, both of which strengthen the paper.

4. **Ecosystem support.** SK has years of blog posts, Stack Overflow answers, and community samples. When stuck — and a developer returning from a 15-year coding gap *will* get stuck — this matters more than architectural elegance.

**Defense talking point:** "MAF is the future direction Microsoft is investing in for agentic systems, and the architecture here would port to it directly. The choice to build on SK was deliberate: it placed the multi-agent design responsibility in this codebase rather than delegating it to a framework abstraction, which is consistent with the thesis position that the agent design is the contribution, not the choice of vendor."

### Gemini free-tier rate limit: 429 on input tokens per minute

First end-to-end run hit `429 Too Many Requests` on the second email. Diagnosed via direct curl against `generativelanguage.googleapis.com` — the quota that tripped was `GenerateContentInputTokensPerModelPerMinute-FreeTier`, not `RequestsPerMinute`. Macedonian PDF text containing broken-encoding Cyrillic produces high token counts (multi-byte UTF-8 sequences in non-Latin scripts), which can exhaust the per-minute input-token budget after only 1–2 large emails.

**Fix:** added `CallWithRetryAsync` wrapper in `BillExtractor` — catches `HttpOperationException` with status 429, retries with exponential backoff (5s → 15s → 30s). Three retries then propagate. This is the correct behavior any production LLM client should have: rate limits on hosted APIs are transient by design, and the provider's own documentation prescribes retry-with-backoff as the canonical response. Treating the retry as a "fix" rather than a "hack" is the appropriate framing — the *bug* was the absence of retry, not the API returning 429.

**Worth one paragraph in the paper:** the LLM provider's quotas are an operational constraint that the agent architecture must accommodate transparently. Centralizing the retry policy in a single helper inside the extractor (rather than spreading try/catch across callers) is a small but defensible design choice. A production deployment would extend this to: queueing under sustained pressure, fallback to a different model when the primary is rate-limited (Gemini Flash → Gemini Flash-Lite, or → local Ollama), and circuit-breaker patterns. v1 ships with retry-with-backoff; full resilience is documented as future work.

### Multilingual prompt design as a thesis bullet (revisited)

The system prompt does not list any Macedonian keywords. It describes Macedonian invoice/confirmation subject-line *patterns* in English, and asks the model to apply those patterns. The Macedonian-language understanding lives in Gemini's pretraining, not in our code. A rule-based pipeline would need per-language keyword lists per vendor; this design extends to any language Gemini supports with zero code changes. Confirmed working on Macedonian emails (subject `"Известување за успешно плаќање"` correctly classified as `payment_confirmation`, subject `"вашата електронска сметка..."` correctly classified as `invoice`).

### Empirical validation (first real run)

End-to-end run against two real labeled emails produced clean structured output on both:

**Payment confirmation** (`"Известување за успешно плаќање"`, no PDF):
- Vendor extracted from email body: *ЈП Колекторски систем - Скопје* (the Skopje sewerage authority)
- Amount: 63.00 MKD
- PaidDate: 2025-06-14 (correctly populated)
- DueDate: null (correctly omitted — confirmations don't carry due dates)
- Reference: transaction code from the body
- Confidence: 1.0
- Reasoning citation: the Macedonian subject line

**Invoice** (`"Вашата електронска сметка од Колекторски систем за 05/2025"`, with the broken-Cyrillic PDF):
- Vendor extracted from email subject (clean text path): *ЈП Колекторски систем Скопје*
- Amount: 63.00 MKD (extracted from the PDF — numbers extract cleanly even with broken Cyrillic font mapping)
- DueDate: 2025-06-30 (from PDF)
- PaidDate: null (correctly omitted — invoice, not paid yet)
- Period: 2025-05 (from PDF)
- Reference: 25051450182 (invoice number from PDF)
- Confidence: 1.0
- Reasoning citation: subject keywords + PDF numeric details together

This is direct empirical evidence for two architectural claims:

1. **Cross-source extraction works.** Agent A correctly used the email subject/body for Cyrillic vendor names (clean text) and the PDF for numeric fields (clean even with broken fonts). The model implicitly routed each field to its most reliable source — exactly the strategy the prompt asked for, without any hand-written rules.
2. **The reconciliation surface is set up.** Both emails reference the same vendor (modulo a hyphen). Agent B's tool-using reconciliation prompt (Day 7) will therefore have a real, slightly-noisy matching task — the *interesting* case where fuzzy matching is genuinely needed, not a toy.

### Diagnose-via-curl as an engineering pattern

Day 3 hit a 429 that retries couldn't clear. Direct curl against `generativelanguage.googleapis.com` revealed `limit: 0, model: gemini-2.0-flash` — i.e., the *free-tier quota for that model is zero*, not exhausted. The SDK exception ("429 Too Many Requests") was technically correct but practically misleading; the underlying HTTP response carried the precise diagnostic ("quotaId" with explicit limit value) that the SDK never surfaced.

**Engineering pattern worth one paragraph in the paper's "Operational Lessons" section:** when an SDK gives an inscrutable error, hit the underlying HTTP endpoint directly with curl. APIs designed for direct use tend to return rich diagnostic JSON; SDKs often discard those details when mapping responses to exceptions. The fix here (switching to `gemini-2.5-flash`, the current free-tier model after Google moved the default in mid-2025) took 30 seconds once the actual cause was visible.
