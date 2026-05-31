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

### Semantic field normalization (Reference vs transaction number vs invoice number)

Different vendors use different terminology for what is functionally the same field — *the unique identifier of this document*. Observed in real data:

- Kolektorski (water): invoice carries `Фактура број`; confirmation carries a long alphanumeric transaction code (`25165KcYBAmoesm9996`).
- Telekabel: invoice carries an invoice number (`04-2026-АГ7262-0`); confirmation email carries *both* a bank transaction ID (`NLB-WEB-...-69f8b319...`) and the same invoice number being paid.

Agent A's system prompt defines a single output field `Reference` semantically ("the identifier of this document"), and Agent A maps each vendor's terminology onto it without a per-vendor lookup table. This is **semantic field normalization**: the LLM understands *meaning*, so the agent's output schema can be vendor-agnostic where a rule-based pipeline would need a manually-maintained dictionary per vendor and per language.

A regex-based extractor handling these three vendors would need at least three separate patterns; adding a fourth vendor would require a code change. The LLM-based extractor generalizes to new vendors at *zero* code cost — only the prompt's general guidance about "identifier semantics" needs to be sound. This is a defensible thesis bullet for the "advantages over rule-based extraction" section.

### LLM non-determinism observed in the wild (and the schema fix)

Two consecutive runs of Agent A on the *same* Telekabel confirmation email returned different `Reference` values:

- Run 1: `04-2026-АГ7262-0` (the invoice number being paid — useful for reconciliation)
- Run 2: `NLB-WEB-АГ7262-20260504-69f8b319ec763` (the bank's transaction ID — useless for invoice matching)

Both are *correct* answers to the literal question "what is the reference number in this email" — the confirmation contains both identifiers, and at temperature 0.1 the model still occasionally picks the bank's ID over the invoice number. This is the canonical kind of LLM-output instability under ambiguous inputs.

**Architectural fix (not a prompt-tuning hack):**
1. Extended `BillExtraction` with a `RelatedReferences: string[]` field for all secondary identifiers.
2. Updated the prompt to explicitly disambiguate: `reference` is the identifier most likely to also appear on the matching counterpart document (the invoice number); `relatedReferences` carries the rest.
3. Agent B's reconciliation (Day 7) does not rely on `Reference` as a strict match key. Instead it reasons over the tuple `(vendor, period, amount)` as the primary signal, with `Reference` and `RelatedReferences` as additional evidence the model can use to break ties.

**Why this is the right pattern, defensible at viva:**

> "Reference equality is fragile because vendors include multiple identifier tokens in their emails and LLM extraction is non-deterministic about which one is primary. Rather than chase prompt engineering, the architecture treats reconciliation as a decision under uncertainty: Agent B receives the full set of extracted identifiers and reasons about which counterpart bill best matches, given vendor + period + amount + any reference overlap. This is precisely where agentic behavior earns its name — a rule-based pipeline would either reject ambiguous matches or false-positive on transaction-ID collisions; the agent makes a graded judgement and either acts or flags for review."

This finding emerged from real data — it could not have been discovered from synthetic test fixtures. Worth one paragraph in the thesis's "Methodology / Implementation Insights" section.

### Polling vs. webhooks: idempotent polling for v1

Each `dotnet run` currently re-processes every labeled email — fine for development, wasteful at runtime. Three tiers of solution:

1. **Idempotent polling (v1, Day 9 in PLAN.md).** Store every processed `gmail_message_id` in the `email_log` table. On each poll, fetch the label's message-ID list (cheap, ~1 Gmail API call), skip IDs we've already processed. Gemini is only invoked on *new* messages. At thesis scale (a few new bills per week per user), this is effectively as efficient as webhooks.
2. **Gmail Pub/Sub push (future work).** Use Gmail's `users.watch()` API to subscribe to label changes; Google pushes notifications to a Google Cloud Pub/Sub topic; the application receives them via an HTTPS endpoint. Truly event-driven, near-zero latency.
3. **ngrok-tunneled webhook (dev-only).** Stand up a public URL for the laptop via ngrok or Cloudflare Tunnel for development.

**Chosen for v1: tier 1.** Reasoning:

- Operationally indistinguishable from webhooks at thesis scale and user-facing latency.
- No public HTTPS endpoint required, no Pub/Sub setup, no JWT verification, no 7-day watch-renewal logic.
- Consistent with the existing "local laptop demo" decision — adding deployment-grade infrastructure for v1 would contradict that scope choice.
- The architecture does not preclude Pub/Sub: the polling worker can be swapped for a webhook handler that calls the same downstream code (Agent A → DB → Agent B → notifications) — *only the ingestion trigger changes*. Same separation-of-concerns reasoning as the `IBillSource` discussion: the agent layer is invariant under input-source changes.

**Future-work paragraph (paste-ready for paper):**

> "v1 uses periodic polling with idempotency on Gmail message IDs as the trigger for the ingestion pipeline. The same architecture supports event-driven ingestion via Gmail's Pub/Sub push notifications: the watch-and-push primitives publish to a Cloud Pub/Sub topic which a webhook endpoint can consume, invoking the existing Agent A → Agent B pipeline unchanged. The migration requires no changes to the agent layer; only the trigger upstream of ingestion is replaced. This separation is enabled by the deliberate architectural choice to keep the agent code independent of the input-source plumbing."

---

## 2026-05-26 — Persistence layer: local Dockerized Postgres, EF Core, hand-written schema

### Pivot from Supabase to local Postgres 17 in Docker

The earlier plan named Supabase as the persistence backend. That choice has been reversed in favour of a Postgres 17 container running on the defense laptop, for three reasons.

First, the thesis demonstration is performed locally during the defense. A managed Postgres service introduces a network dependency between the laptop and the demonstration outcome — and the free tier of Supabase suspends idle projects after a period of inactivity, requiring a wake-up handshake of up to thirty seconds. A demonstration that can stall on cold-start latency in front of a committee is a demonstration with avoidable risk.

Second, the schema itself is portable. Both engines speak standard PostgreSQL, the migration script in `db/migrations/0001_init.sql` is dialect-neutral SQL, and redeploying to a managed Postgres (Supabase, Neon, or AWS RDS) reduces to changing one connection string. The local choice does not foreclose a hosted deployment; it simply postpones it past the defense.

Third, the local choice keeps the system reproducible. A future reader who clones the repository can run `docker compose up -d`, apply the init script automatically through Docker's `docker-entrypoint-initdb.d` mechanism, and have a working database in seconds. There is no external account, no API key, and no opaque platform configuration to mirror — the database, like the source code, is fully described by the repository.

### ORM choice: Entity Framework Core 9 (Npgsql provider), schema authored by hand

The ORM is Entity Framework Core 9 with the Npgsql provider. The DbContext maps onto a hand-written SQL migration file (`db/migrations/0001_init.sql`) rather than onto generated `dotnet ef migrations`. This is a deliberate inversion of EF Core's usual code-first workflow.

The reasoning is twofold. The first reason concerns thesis defensibility: a hand-written DDL script can be read aloud line-by-line during the defense, and the integrity constraints — the `UNIQUE` index on `gmail_message_id`, the `CHECK` constraint on `bills.status`, the JSONB columns for `related_references` — are visibly the author's design decisions rather than artifacts of a code generator. A committee question of the form "why is this column constrained this way?" admits a direct answer.

The second reason concerns operational simplicity: for a three-table schema there is no benefit to the migration-snapshot machinery EF Core layers on top of generated migrations. The cost of maintaining `__EFMigrationsHistory` and the model snapshot files exceeds the value they provide at this scale. EF Core is retained for what it does well — typed LINQ queries against entity classes, value converters for the JSONB columns, transaction management — without paying for what it does badly at small scale.

### Idempotency, in detail

The system polls Gmail every five minutes (Day 9). Idempotency is therefore not optional; it is the contract that makes polling safe. Three mechanisms enforce it in layers, from cheap to authoritative:

1. **Application-level precheck.** Before invoking the LLM for a Gmail message id, the worker queries `email_log` for that id. If present, the message is skipped — saving an LLM call and ensuring deterministic re-runs across worker restarts.

2. **Database-level uniqueness.** The columns `email_log.gmail_message_id`, `bills.gmail_message_id`, and `payments.gmail_message_id` each carry a `UNIQUE` constraint. If a concurrent worker iteration races past the precheck, the second `INSERT` raises Postgres SQLSTATE `23505` (unique violation). The repository catches that specific error and treats it as a no-op.

3. **Transactional grouping.** All three writes for a given message (the `email_log` row plus a `bills` or `payments` row when applicable) occur in a single EF transaction. The system never observes a `bills` row without its corresponding `email_log` row.

The layered design follows the standard "optimistic precheck, pessimistic database constraint" pattern. The precheck is the happy path; the constraint is the safety net.

### `email_log` as audit trail, not derived data

The schema contains a deliberate redundancy: every processed email — even those classified as `"other"` by Agent A — produces a row in `email_log`, with the full extraction record stored as JSONB in `raw_extraction`. This duplicates information that already lives in `bills` and `payments` for messages that are also classified as invoices or confirmations.

The duplication is intentional. `email_log` is the audit and replay surface: it answers the question "did the system see this email, and what did the LLM decide about it?" without consulting the downstream tables. During the defense this distinction matters — it lets the demonstration replay LLM decisions against the database after the fact, and it lets a future maintainer (or thesis reader) audit the system's behavior on edge-case messages that produced no business-domain row.

---

## 2026-05-26 — Sheets as derived view, not source of truth

### The role of the Google Sheet in the architecture

The system ships data to a Google Sheet as the user-facing surface — the place the author actually opens to see "what bills are outstanding this month, what did I pay, what's the yearly total." This is the same role the sheet plays in the manual workflow the system is replacing; preserving it lowers the change-management cost for the user, and gives the committee a tangible artifact to look at during the defense.

A design question worth being explicit about: **what is the source of truth?** The answer is Postgres. The Google Sheet is a *derived projection* of the `bills` table, materialized at insert time and (from Day 8 onward) updated on status changes. The schema in `db/migrations/0001_init.sql` is the authoritative description of what the system knows; the sheet is the same data, rendered for a human reader.

This distinction matters because it justifies the failure-handling choice. The append-to-sheet call sits *outside* the database transaction that writes `email_log` and `bills`. If the Sheets API call fails — network blip, quota exceeded, authentication expired — the bill is still safely persisted in Postgres and the error is logged. A future re-sync (manual today, scripted later) can rebuild the sheet from the database. The opposite arrangement — wrapping the sheet write inside the DB transaction — would be wrong: a transient Sheets failure should not block ingestion.

### Future work: the outbox pattern

A production system would not perform side effects (HTTP calls to Sheets, future Telegram pushes from Agent C) directly after a database commit. It would record the *intent* to perform that side effect in an outbox table inside the same transaction, and a background dispatcher would consume the outbox and execute the calls with retries. This decouples ingestion latency from third-party API latency and gives the system at-least-once delivery semantics for derived projections.

For the thesis demonstration this pattern is overkill: ingestion runs every five minutes, the user reviews the sheet visually, and a missed row will be re-attempted on the next poll cycle once we extend the sync to also include "already-persisted bills missing from the sheet." The architecture admits the outbox upgrade as a future-work paragraph without requiring rework of the agent layer — the agents communicate through the database, which is exactly the boundary the outbox pattern would re-use.

### Why OAuth scope is separated from Gmail's

Gmail and Sheets use the same Google Cloud project and the same OAuth client credentials file. They do *not* share a token: each scope (Gmail readonly, Sheets read-write) gets its own refresh token, stored in a distinct on-disk directory (`secrets/token_store` vs `secrets/token_store_sheets`). The separation respects the principle of least privilege at the token level — a malicious read of one token directory does not grant the other capability — and it matches the OAuth consent UX the user has already seen, where each scope is presented in its own consent screen.

---

## 2026-05-26 — Where agents belong, and where they don't: the visualization boundary

A design question surfaced near the end of Day 5: *should an agent manage the Google Sheet?* The intuition behind the question is correct and worth taking seriously. The thesis is about agentic systems; it would be odd if the sheet integration were a thirty-year-old pattern of typed HTTP calls from a deterministic service. The instinct to push more of the system into the agent layer is, in general, the right instinct for this thesis.

But the instinct has limits, and the limits are the most defensible part of the architecture.

### The failure mode of letting agents do layout

If an LLM is given the authority to design and arrange the sheet — to choose columns, group rows, apply conditional formatting, lay out monthly and yearly aggregates — every run is a fresh design decision. The first run produces one layout; the second run, primed by a slightly different invoice, produces a marginally different one. Cells that the prior conditional-formatting rule pinned by range no longer fall under that rule. Pivot tables drift. The dashboard the author screenshots for the defense slides is not the dashboard the system produces during the live demonstration.

This is not a hypothetical concern. It is the standard failure mode of agentic-everything architectures: the system works in nine demonstrations and falls over in the tenth because the non-determinism that makes the LLM useful in classification also makes it unreliable as a UI layout engine.

The deeper point is conceptual. Layout and aggregation are not agentic tasks. They are spreadsheet tasks. Pivot tables and conditional formatting exist precisely because they are deterministic projections of a tabular source. Delegating them to an LLM is delegating a solved problem to a non-deterministic solver — a category error, not a stylistic one.

### Two surfaces, one source

The architecture adopted here resolves the tension by separating the surfaces. The system writes to a single, normalized tab named `Bills`: ten columns, one row per invoice, append-only, deterministic, populated by `SheetsWriter` after every successful database commit. This is the projected audit log. It is visually boring on purpose; it never rearranges; every row is traceable to a Gmail message id.

The human-facing dashboard — monthly and yearly totals per vendor, paid-versus-unpaid coloring, grouped averages, running aggregates — lives on a separate tab built once, by hand, in Google Sheets. It consists entirely of standard spreadsheet primitives: `SUMIFS` for grouped totals, `AVERAGEIFS` for per-vendor averages, a conditional-formatting rule keyed on the `Status` column that paints rows green for `paid`, yellow for `needs_review`, and leaves `pending` unstyled. The system never touches this tab. It refreshes automatically because its formulas reference the `Bills` tab the system *does* write.

The committee question "how does the dashboard stay current?" has a one-sentence answer: "The agent populates a normalized base table; the visualization is standard spreadsheet formulas referencing that table." The committee question "what happens if the layout drifts?" has the same answer: it cannot, because the system does not produce the layout.

### Where the agents legitimately use tools

The architectural restraint argued for here is not that agents should avoid tools. It is that agents should use tools in the places where reasoning is the bottleneck, and stay out of the places where determinism is the requirement.

Agent B, the reconciler (Day 7), is given a `mark_bill_paid` tool. When it concludes — by reasoning over vendor, period, amount, and reference strings — that a payment matches a bill, it invokes that tool, which performs two side effects: it updates the `bills.status` column in Postgres, and it updates the corresponding `Status` cell in the `Bills` tab. The agent's job is the reasoning that *decides* the match; the tool's job is the deterministic write that *executes* the match. This is the textbook division of labor in agentic systems, and it produces a demonstrable, defensible artifact: a single status cell flips, the conditional formatting rule paints it green, and the committee sees an agent's reasoning translated into a visible state change.

Agent C, the conversational interface (Day 10, Telegram), is given query tools: `query_yearly_total(vendor, year)`, `query_unpaid_bills()`, `query_monthly_summary(month)`. These tools read from Postgres, not from the sheet, because the questions they answer ("what is my yearly total for vendor X") are database questions expressible as parameterized SQL. Reading from the sheet to answer them would be reading from the *projection* when the *source* is available — the same category error as letting the agent lay out the projection in the first place, run in reverse.

### What the thesis claims, and what it does not

The contribution of an architecture like this one is not that agents do everything. It is that agents are deployed precisely where reasoning is the differentiator — classification of unstructured email, reconciliation across noisy identifiers, conversational query over heterogeneous data — and that the rest of the system remains deterministic plumbing. The literature contains many agentic-everything systems; the contribution of this one is the discipline of the boundary.

Stated as a paragraph for the paper:

> "The system deploys language models in three roles — extraction, reconciliation, and conversational query — each backed by a narrow tool surface that performs deterministic side effects on a normalized database. Visualization, layout, and aggregation are delegated to the spreadsheet's native formula and conditional-formatting capabilities operating on that database's projection. This separation prevents the non-determinism of agent reasoning from leaking into the user-facing surfaces of the system, while preserving the agentic character of every task where reasoning, rather than computation, is the bottleneck."

### A note on MCP

The question of whether to expose the sheet through the Model Context Protocol was raised and deferred. MCP is well-suited to connecting general-purpose agents (such as Claude Desktop) to external tools they could not otherwise reach. For a closed system where the author controls both the agent and the tool — as is the case here — MCP would introduce an indirection layer whose value is not realized within the bounded scope of the demonstration. The thesis records MCP as a deployment path for future work: the same `mark_bill_paid` and query tools could be exposed as an MCP server, enabling generic agentic clients to drive the system without modification to its core.

---

## 2026-05-27 — Agent B (the reconciler): tool surface and matching architecture

This entry captures the design discussion that preceded Day 7 implementation. It is the architectural centerpiece of the thesis — the place where the system stops being an extraction pipeline and starts being an agentic one — and the choices made here are the choices most likely to come up in defense Q&A.

### What Agent B is for

Agent A converts an email into a structured record. That is a pure extraction problem: input is unstructured text and PDF, output is a typed object, the transformation has no opinion about what came before or what comes next. No tools, no state, no reasoning over the system's existing knowledge. It is a "function" in the most literal sense.

Agent B is different in kind. Given a payment confirmation that just arrived, it must decide whether that payment settles a bill the system already knows about. The decision requires reading the existing state of the system (the set of pending bills), reasoning over identifiers that are noisy and inconsistent across the invoice/payment pair, and writing a state mutation if a match is found. This is the canonical shape of an agentic task: read state, reason, act. The same task expressed as deterministic SQL would require either an unrealistic assumption that vendor strings and reference codes match exactly between invoice and payment (they do not, as the test inbox demonstrates), or a hand-coded fuzzy-match heuristic specific to each utility company's quirks. The LLM is justified here because the variability is real and the rules cannot be written down in advance.

### The tool surface: four narrow tools, not one broad one

A coherent alternative would have been to give Agent B a single broad tool — "execute SQL against the bills table" — and let the model write its own queries. This is the path of maximum agentic freedom. It was rejected. Four narrow tools were chosen instead:

`list_pending_bills_for(vendor, period, amountMin, amountMax)` retrieves up to ten candidate bills using a SQL filter that maps directly to the composite index `idx_bills_vendor_period_amount` created on Day 4. The vendor parameter is a case-insensitive substring (Postgres `ILIKE '%token%'`) so that the model can pass a distinctive core token rather than the full vendor name, accommodating the vendor-string variability observed in the test data.

`mark_bill_paid(billId, confidence, reasoning)` is the success path: it mutates `bills.status` to paid and `payments.matched_bill_id` to the bill id, in a single EF SaveChanges. The `touch_updated_at` trigger fires automatically, so the Sheets projection (Day 8) will see the change on its next read.

`flag_bill_needs_review(billId, confidence, reasoning)` is the ambiguous path: a candidate plausibly matches but the model cannot commit at the confidence threshold. The bill is flipped to `needs_review`; the payment's `matched_bill_id` is intentionally left NULL so that a human (or Agent C on Day 10) can complete the match.

`flag_payment_unmatched(reasoning)` is the orphan path: no candidate bill plausibly matches. No DB mutation; the payment remains visible to Day 10's notification flow.

The narrowness of this surface is itself a thesis claim. The retrieval tool encodes the matching heuristic at the SQL level (vendor substring + period + amount window), and the three terminal tools encode the decision taxonomy at the API level (matched / ambiguous / unmatched). The LLM reasons inside the space these tools define, but cannot reason *outside* it. A broader tool surface would have given the model more freedom and given the defense more questions to answer about what it does with that freedom. A narrower tool surface is easier to defend, easier to test, and produces a more predictable demonstration.

### Tight retrieval, loose reasoning

The matching architecture follows a pattern common in production agentic systems: the tool layer narrows the search space deterministically; the reasoning layer chooses from the narrowed set. SQL retrieves up to ten candidates matching vendor (substring), period (optional exact match), and amount (configurable window, typically ±1.00 currency unit). The model then reads the candidates and decides which (if any) matches the payment, using whatever signals are available — vendor semantic identity, period-vs-paid-date adjacency, exact amount, shared reference tokens.

This pattern resolves the precision/recall tradeoff cleanly. A pure-SQL approach (vendor exact, period exact, amount exact) misses real matches because invoices and payment confirmations disagree on vendor strings ("ЈП Колекторски систем" vs "ЈП Колекторски систем - Скопје", from the test inbox) and on which reference token is treated as primary. A pure-LLM approach (model browses all pending bills) wastes tokens and produces more false positives. The hybrid — fuzzy SQL filter narrowed by indexed columns, semantic decision by the model — gets recall from the retrieval and precision from the reasoning.

### Two-layer confidence: the agent decides, the system also decides

The confidence threshold of 0.85 lives in two places that must agree. The system prompt instructs the model that confidence below 0.85 should be recorded via `flag_bill_needs_review` rather than `mark_bill_paid`. The C# implementation of `MarkBillPaidAsync` checks the threshold again and, if a low-confidence call comes through, automatically downgrades it to a `needs_review` outcome and logs the downgrade as a warning.

The defense argument for the duplication is defense-in-depth. The model is told the rule and is expected to follow it; the runtime enforces the rule regardless. The duplication is not a vote of no confidence in the model — the warning log fires almost never in practice, because the model follows its instructions — but it eliminates a class of failure mode (an overconfident model mutating the database based on a weak match) without requiring trust in the model's calibration. When the committee asks "what stops the LLM from being wrong with high confidence?", the answer is: nothing stops it from being wrong, but the runtime stops it from acting on a confidence it did not earn.

### Sweep mode, not per-payment ingestion-time reconciliation

Agent B runs as a separate pass after ingestion finishes, not synchronously after each payment is persisted. The architecture is therefore: ingest all emails, persist all payments, then sweep over `WHERE matched_bill_id IS NULL` and reconcile each one in turn.

The alternative — kicking Agent B inline as soon as a payment lands — would couple ingestion latency to reconciliation latency and make the demonstration harder to narrate. Sweep mode is easier to talk about ("the system reconciles periodically") and easier to demonstrate ("watch Agent B run alone on demand against existing data"). It also matches the temporal reality of the domain: payment confirmations and invoices can arrive in either order, with hours or days between them, so reconciliation should not be a same-message operation in the first place.

### What the prompt encodes

The matching heuristic is described in the system prompt in natural language, not in code. The choice is deliberate: the rules for matching are reasoning rules, not pattern-matching rules. They include things like "if the invoice period is N and paid_date falls in month N, N+1, or N+2, that is a normal payment timeline, not a no-signal." Encoding rules of this shape in code requires either committing to specific deterministic tolerances (which over-fit on the training data and break on the long tail) or writing a small DSL for fuzzy temporal predicates (which is over-engineering). The prompt is the spec, and the spec is reviewable by a human reader — the committee can read it during defense and form an opinion on whether the rules are reasonable.

The prompt includes four worked examples drawn from the actual structure of test inbox data (two clean matches with different difficulty profiles, one ambiguous case, one no-candidates case). The examples are not synthetic; they describe the failure modes the system was designed to handle, made concrete with field-level detail. Their presence in the prompt is the only "training" the model receives for this task.

---

## 2026-05-27 — First-run validation: the agent justified itself in its own rubric

The Reconciler architecture was validated on first execution against the test inbox without modification. The result is worth recording in detail, because the form of the agent's output — not merely its correctness — is the thesis claim made visible.

### The two test cases

The test inbox contained two invoice/payment-confirmation pairs, deliberately chosen to exercise different difficulty profiles of the matching problem.

The first pair was a Телекабел invoice for the 2026-04 period, amount 1406.00 MKD, invoice number `04-2026-АГ7262-0`, and the corresponding payment confirmation forwarded from an NLB bank notification, carrying the same invoice number verbatim in its reference field. This is the *easy* case: the vendor string is identical on both sides, the amount is exact, and the invoice number appears verbatim in both records. A naive deterministic matcher would handle this correctly. The case is included not because it tests the agent but because it tests that the agent does not invent friction where none exists.

The second pair was substantially harder. The invoice was issued by `"ЈП Колекторски систем"` for the 2025-05 period, amount 63.00 MKD, with invoice number `25051450182`. The corresponding payment confirmation, however, recorded the vendor as `"ЈП Колекторски систем - Скопје"` — the same legal entity, written with the city suffix appended. The two strings are not equal. A naive SQL `WHERE vendor = vendor` match would fail; the rows are not joinable by string identity. This is the case the thesis is about.

### What the agent did

On the Телекабел case, Agent B called `list_pending_bills_for(vendor="Телекабел", period=null, amountMin=1405.00, amountMax=1407.00)`, received one candidate, and immediately called `mark_bill_paid` with confidence 0.97 and the reasoning string:

> "Vendor, amount, and reference are exact matches; paid_date aligns with bill period."

On the Колекторски case, the agent's filter choice is the interesting part. It did not pass the full vendor string seen on the payment ("ЈП Колекторски систем - Скопје"). It passed the core token "Колекторски" — exactly as the system prompt had instructed it to do — yielding one candidate (the invoice with the shorter vendor variant), which it then accepted with confidence 0.95 and the reasoning string:

> "Vendor semantic match, exact amount, paid_date in N+1 month, and invoice reference '25051450182' appears verbatim in payment.reference."

### The form of the reasoning is the thesis claim

Both reasoning strings deserve attention not because they are correct (correctness was confirmed by the resulting database state, not by the prose) but because of *what vocabulary the agent chose to explain itself in*.

The system prompt instructs the agent to use "semantic identity, not string equality" for vendor names, calls the period adjacency concept "N+1 month grace," and uses the word "verbatim" to describe shared reference tokens between payment and invoice. The Колекторски reasoning string contains all three terms: "vendor semantic match," "paid_date in N+1 month," "verbatim." The agent did not generate an arbitrary explanation in arbitrary vocabulary; it generated an explanation in *the rubric the prompt taught it*, and used that rubric to justify each component of its match decision against each criterion in the spec.

This is the demonstrable property that distinguishes an agent from a classifier. A classifier returns a label. An agent returns a label *and an account of its reasoning*, and when the reasoning is expressed in terms the designer can read and the designer's spec is also expressed in those terms, the system becomes *audited by inspection of its own output*. The thesis claim is not that LLMs can match utility bills (they can, and that is unsurprising); the thesis claim is that a system designed in this shape produces traceable, justifiable, defensible decisions, and that the design is realizable in practice without exotic infrastructure — Semantic Kernel, a free-tier model, four narrow tools, and a thoughtfully written prompt.

### Calibration without intervention

Both decisions landed in the confidence band the prompt specified for `mark_bill_paid` (≥ 0.85). The Колекторски case, despite being the harder of the two — vendor variant, no shared period field, only a shared invoice number to disambiguate — was rated 0.95 by the agent. The Телекабел case, with identical vendor strings and shared reference, was rated 0.97. The two-point gap between them mirrors the relative difficulty of the cases as a human reader would assess them: both confident, both safe to commit, the Телекабел case marginally cleaner.

The defense-in-depth confidence threshold in `MarkBillPaidAsync` (0.85 in C#, mirroring the 0.85 floor in the prompt) was never triggered. The runtime safety net exists to catch a misbehaving agent; in practice on real data the agent stayed inside its own calibration band, and the warning log path remained cold. This is the desired equilibrium: the runtime check is silent infrastructure, audited but unused, present in the architecture precisely so that one cannot argue the system trusts the model unconditionally.

### Why this matters in the defense

This first-run validation is the demonstration paragraph for the agentic-systems chapter of the thesis. The committee can be shown three artifacts in sequence: the prompt (the spec, in natural language); the agent's reasoning string (the justification, in the same vocabulary as the spec); the database row (the resulting state mutation). Each artifact is human-readable. Each one corresponds to a step the model took. The fact that the vocabulary chains across all three — "semantic match" appears in the spec and in the reasoning, the state mutation reflects the decision the reasoning explains — is what distinguishes a defensible agentic system from a black-box LLM call wrapped in plumbing.

---

## 2026-05-27 — Sheet upsert on status change (Day 8): why this is not a fifth tool

A natural reflex when extending the Reconciler — and the one that came up first — would have been to give Agent B a fifth tool called `update_sheet_status`. The argument for it is symmetric and superficially clean: the agent decides the status, so the agent should write the status to every surface that displays it. The argument was rejected.

The reason is a direct application of the architectural-restraint claim recorded earlier in this document. The thesis position is that agents are deployed where reasoning is the bottleneck, and stay out of the places where determinism is the requirement. *Deciding* whether a payment pays a bill is reasoning. *Updating a cell in a spreadsheet given a known bill id and a known new status* is not — it is a deterministic function of two values. The four-tool surface defended in the earlier entry would have grown to five for no reasoning gain, and the narrowness claim — already a defense talking point — would have been weakened by accommodation.

The chosen alternative keeps the surface at four. The `ReconcilerAgent` orchestrator, after the agent's chat loop completes and one of the three terminal tools has recorded an outcome, inspects `toolset.Outcome.BillId`. If a bill id is present (matched or ambiguous outcomes), the orchestrator fetches that bill's `gmail_message_id` and current `status` from Postgres and calls `SheetsWriter.UpdateBillStatusAsync` itself, in deterministic .NET code, outside the agent's view. The agent does not know the sheet exists; it does not have to know.

This is the same pattern Day 5 already established for `AppendBillAsync`: the agent extracts the bill, the system projects the bill to the sheet, and the projection is decoupled from the reasoning that produced the bill. Day 8 extends the same pattern from the insert case (Agent A) to the update case (Agent B). Failure of the sheet update is logged and tolerated, because Postgres is the source of truth and the sheet can be re-synced from it on the next run if it falls out of step. The narrative the committee hears, end-to-end, is consistent: agents reason; the system projects; the database is canonical; the sheet is a view.

### Implementation: read column J, write column G

The update is implemented as a two-call sequence against the Sheets API. The first call reads column J of the `Bills` tab (the `Gmail Msg Id` column added to the header on Day 5) and the orchestrator scans it for the message id of the bill whose status changed. The second call writes the new status into column G of the matched row. Two round-trips per update is acceptable at reconciliation throughput, which is bounded by the rate of incoming payment confirmations — measured in single digits per day on the test inbox.

For a higher-volume deployment the right move is batched: read the full J column once at sweep start, accumulate `(row, status)` pairs as outcomes are recorded, and write them with a single `BatchUpdate` call at sweep end. The current implementation does not do this because the demonstration scale does not require it, and a batched implementation would obscure the read-modify-write logic that is easier to read in its current sequential form. The trade is recorded here so the thesis can cite the simpler implementation honestly without claiming it is the only one possible.

### Visual consequence: the demo loop closes

The user-facing consequence of this change is that the `Status` column in the `Bills` tab now reflects the agent's reconciliation decisions in real time. A conditional-formatting rule that the user maintains by hand on the `Status` column — `paid` → green, `needs_review` → yellow, `pending` → no formatting — produces the visual change the committee will see during the demonstration: an email arrives, a row appears in the sheet with status `pending`, the reconciler runs, the cell turns green. The full visible loop closes inside one execution of the worker process, on real data, without any code in the visualization path having opinions about layout or color.


---

## 2026-05-28 — Continuous polling loop (Day 9): why polling, why interval, why configurable

Day 9 converts the worker from a single-shot ingest script into a long-running service. The change is small in lines of code — a `while` loop, an OAuth initialization lifted out of the body, a per-tick try/catch — and large in what it says about the system's posture. The worker now is the thing that is running; the human is the thing that occasionally forwards email at it.

### Why polling and not webhooks

Gmail offers a push-notification channel via Cloud Pub/Sub, and a defensible-looking design could route a Pub/Sub topic at the worker over a public HTTPS endpoint, eliminating the polling cost entirely. This was considered and rejected on the same grounds recorded earlier in this document for the Supabase-versus-local-Postgres pivot: the public endpoint requires a deployment surface (a TLS certificate, a public DNS name, a tunneling tool or a hosted environment) that the thesis project does not have and does not need. Polling at the chosen granularity is well inside Gmail's per-user quota and well inside the latency tolerance of utility bills, which arrive on the timescale of days, not seconds.

The trade is recorded honestly: a production deployment in a setting where bills must be visible within seconds of arrival would migrate to push. Nothing in the current architecture would have to change except the entrypoint — the per-tick body (`RunTickAsync`) is already the natural unit of work for a push handler. The design is push-ready, not push-implemented.

### Why interval polling and not cron-style scheduling

A second instinct, when the cadence question was raised, was to introduce a cron expression — "run at 09:00 every day" — configured as a string in `.env` and interpreted by a scheduler library such as NCrontab or Cronos. The instinct was correct in observing that "once a day" is the right *production* cadence for a system that ingests utility bills; it was wrong in concluding that cron is the right *mechanism*. The two are independent decisions, and the latter is the more consequential one.

The mechanism chosen is a polling interval expressed as a `TimeSpan`, read from `BILLAGENT_POLL_INTERVAL` in `.env`, defaulting to one day. The interval is the time the worker sleeps between ticks, measured from process start, with no alignment to wall-clock boundaries. The dependency cost is zero — `TimeSpan.Parse` is in the standard library — and the configuration surface is a single value that means exactly what it says.

The argument for choosing interval polling over a cron expression is the defense scenario specifically. A cron-scheduled worker that runs at 09:00 daily cannot be demonstrated by forwarding a bill to it at 14:00. An interval-polled worker configured at thirty seconds can. The committee will see the forward, the tick, the agent's reasoning, and the sheet update inside a single coherent demonstration window. The same code, configured with `BILLAGENT_POLL_INTERVAL=1.00:00:00` in production, runs once per day; configured with `BILLAGENT_POLL_INTERVAL=30` for the defense, runs every thirty seconds. The mechanism is unchanged; only the configuration varies. A cron-style design is deferred to the same category as Pub/Sub push notifications: a production-deployment concern that the thesis project deliberately does not implement, because the interval-polling design covers the demonstration scope without buying capability the demonstration does not need.

### Idempotency is not new on Day 9

The polling loop is safe to run on the same inbox forever because `BillRepository.HasProcessedAsync` consults the `email_log` table on each candidate message and skips any whose `gmail_message_id` is already present. The unique constraint on `gmail_message_id` introduced on Day 4 is the structural reason the loop terminates correctly at the end of each tick — there is no "where to resume from" bookkeeping, because the database itself is the resume cursor. This is the dividend the schema choice on Day 4 was made to pay. Day 9 cashes it.

The opposite design — keeping a `last_seen_message_id` cursor in worker memory or in a config file — would have produced exactly the same behaviour in the happy path and a near-undebuggable bug the first time the worker crashed mid-tick. The database-as-cursor design has no such failure mode: a tick that crashes after persisting some messages and before persisting others leaves the persisted ones in `email_log` and the unpersisted ones absent from it, and the next tick picks up precisely the missing ones. Crash safety here is not a feature added; it is a feature that follows from the schema.

### Failure is now an event, not a terminal condition

The pre-Day-9 worker had one try/catch in `ExecuteAsync`. The catch logged the exception and the `finally` called `_lifetime.StopApplication()`. The Day 9 worker keeps a critical-failure escape hatch for the OAuth initialization step — no point looping if we cannot talk to Gmail at all — and otherwise treats every per-tick exception as a transient condition to log and retry. This is the canonical posture for a polling service and the reason the worker can survive a Gmail 5xx, a Postgres reconnect, a Gemini rate limit, or a Sheets API blip without manual intervention.

The trade-off recorded here is that *silent* failures become possible — a bug that always throws on a particular malformed PDF will throw on every tick forever, filling the logs and never persisting that bill. The intended response is the `needs_review` status path on the Agent A side (Day 11), at which point the malformed PDF is captured as a database row with low confidence instead of a recurring exception. Day 9's loop is, in that sense, a forward bet on Day 11's robustness work.

### Why the sweep runs every tick

A natural micro-optimization would be to skip Agent B's sweep on ticks where no new messages were ingested. It was rejected. Agent B reconciles based on the state of the `bills` and `payments` tables, not on the events of the current tick. A payment confirmation that arrived two ticks ago and could not be matched at the time — because the invoice it pays had not yet been ingested — must still be revisited on every subsequent tick until the invoice arrives. Coupling the sweep to "ingested something this tick" would mean a payment that arrives after its invoice goes matched, and a payment that arrives before its invoice stays unmatched indefinitely. The sweep is cheap when the unmatched-payment set is empty (one SQL query returning zero rows) and necessary when it is not.

The visible side of the trade is purely cosmetic: the sweep header is printed only on ticks where at least one new message was ingested, so the steady-state log stays quiet. The sweep itself runs unconditionally.


---

## 2026-05-29 — Telegram bot + Agent C (Day 10): the system gets a face

Day 10 attaches the system to a user. Before today, the worker existed in two private surfaces: a Postgres database and a Google Sheet. Both required the operator to look at them deliberately. The Telegram interface inverts that posture — the system now reaches *out* to the user when something interesting happens, and the user can reach *in* with natural-language questions whenever they want.

This day was significantly larger than the previous nine, both in lines of code and in design decisions, and the temptation to land it as one undifferentiated commit was strong. The work was structured deliberately as two halves: the *push* side (the system speaks first), and the *query* side (Agent C answers). They share no code except a `TelegramNotifier` instance and a single chat-id whitelist; they are otherwise independent subsystems.

### Why a chat interface replaces the Blazor dashboard

The earlier plan, recorded earlier in this document, was a Blazor server-side dashboard rendering the bills table with status colours and filters. That plan was discarded for two reasons that compounded.

The first was deployment surface: a Blazor app needs a host, a Kestrel port, a reverse proxy if it should be reachable outside `localhost`, and authentication if it should be safely reachable at all. None of that is part of the thesis claim. A Telegram bot needs only an outbound HTTPS connection from a single .NET process to `api.telegram.org`, with no inbound ports, no certificates, no proxy. The lower deployment cost is not a convenience — it is the difference between a demonstrable artifact and a system that requires the operator to be a sysadmin during the defense.

The second reason was alignment with the thesis. The Blazor app would have been a *view* — code that renders database rows. The Telegram bot, in the form chosen, is not just a view: half of it is *Agent C*, a second LLM-driven agent with its own tool surface. The earlier dashboard would have demonstrated good .NET engineering; the bot demonstrates that the agentic pattern explored with Agent B generalises to a second use case with different reasoning constraints (read-only retrieval over user-facing natural language, rather than write-side matching over noisy identifiers). The thesis claim that the agentic shape is general is supported more directly by a second agent than by a templating engine.

### Two BackgroundServices in one process

The pre-existing `Worker` is a `BackgroundService` that ticks at a configurable interval (Day 9). The Telegram interface was added as a *second* `BackgroundService` — `TelegramBotHost` — registered with the same generic host. The two run as independent loops with no coupling at the lifecycle layer. This was a deliberate choice over interleaving inbound Telegram messages into the ingest tick.

The argument is one of cadence asymmetry. The ingest tick wants to be slow and batched — once a day in production, once every thirty seconds in the demo. The bot wants to be live-responsive, with sub-second latency from the user typing a question to receiving an answer. Interleaving the two would force either the ingest to run as fast as the bot needs to respond (wasted Gmail quota), or the bot to wait until the next ingest tick to acknowledge an inbound message (unusable user experience). Keeping them as separate hosted services lets each run at its own natural cadence inside one process, sharing dependency-injected state (database, configuration, notifier) without sharing a control loop.

### The push side: projection, not action

Three event classes trigger an outbound Telegram message: a new invoice ingested by Agent A, a payment reconciled by Agent B (the three terminal outcomes — matched, needs review, unmatched). In every case the agent that produced the event does *not* know Telegram exists. The orchestrator code that ran the agent calls `TelegramNotifier.SendAsync(...)` *after* the agent's chat loop has completed and its decision is recorded in Postgres. The message text is composed by the orchestrator in plain .NET code, formatted with a single leading emoji per event type for visual scannability, and the agent's reasoning string is appended only for the ambiguous and unmatched cases where the user benefits from seeing the model's account.

This is the same restraint-of-tool-surface argument made for the Sheet on Day 8, applied to a second projection surface. The agent reasons; the system projects. The four-tool surface of Agent B and the five-tool surface of Agent C are both narrow on purpose, and would have grown by one each if "send Telegram message" had been added to either of them. The narrowness is preserved by performing the projection in the orchestrator, outside the agent's view.

A `try/catch` around every send makes the projection fail-soft: Postgres is the source of truth, and a Telegram outage produces a logged error rather than a propagated exception that would corrupt the ingest pipeline. The same posture as the Sheet projection on Day 5/8.

### Agent C: read-only is the trust argument

Agent C exposes five `[KernelFunction]` methods on its `QueryTools` plugin: `list_bills`, `bill_status`, `monthly_summary`, `unpaid_count`, `yearly_total`. Every one of them is a `SELECT`. None of them mutates anything. This was a sharp choice and worth recording the argument for, because the most-natural extension of the design — giving Agent C a `mark_paid` or `dismiss_bill` tool — was considered and rejected.

The argument is the same as Day 8's argument against giving Agent B a fifth `update_sheet_status` tool, with a sharper edge. Day 8's argument was about reasoning gain; this one is about trust surface. Agent C's interface is reachable by anyone who can DM the bot. The bot's username is discoverable to the entire Telegram user base. A whitelist gates inbound traffic, but the *worst case* in the design — a misconfigured whitelist, a copy-paste mistake, an accidental commit of a misconfigured environment — must remain bounded. A read-only agent in the worst case surfaces information the user already owns; a write-capable agent in the worst case mutates the user's bill records on a stranger's instruction. The asymmetry of consequence justified the asymmetry of capability.

The implication for the thesis is that the *write* paths in this system are concentrated in exactly one place — Agent B's three terminal tools — and the *read* paths are exposed liberally through Agent C. The two agents share the same Postgres schema, the same Semantic Kernel, the same `gemini-2.5-flash` model, the same retry harness. Their difference is purely in tool surface, and that difference encodes the trust model. This is a structurally cleaner story than "we have one agent that does everything, gated by prompt instructions" — the kind of design that the committee would reasonably ask about.

### The whitelist as trust boundary

A Telegram bot is world-accessible by construction. The bot's username is enough to start a conversation; there is no permission gate at the protocol level. `TelegramWhitelist` is therefore a hard prerequisite of the design, not an enhancement deferred to "production."

The whitelist is parsed from `BILLAGENT_TELEGRAM_ALLOWED_CHAT_IDS` (comma-separated longs) at startup. The first id on the list serves a second role: it is the destination for *outbound* push notifications. This collapses two configuration values into one and avoids the failure mode of a system that pushes to one chat but accepts queries from another. The same id authorises both directions.

The bootstrap problem — the operator needing to know their own Telegram chat id before they can configure the whitelist — was solved without a third-party id-discovery bot. When the whitelist is empty, the bot host logs every inbound message with its `chat_id` prefixed by `[BOOTSTRAP]` and replies to the sender with their own id and an instruction to ask the operator to add it. The first run is therefore: leave the variable empty, `dotnet run`, send `/start` to the bot, read the chat_id from the console, paste it into `.env`, restart. The full setup takes under a minute and requires no extra tooling.

Rejected messages (whitelist populated, sender not on it) get the same "you are not allowed, your id is X" reply. This is a deliberate disclosure — the sender already knows their own chat id (the protocol exposes it), so the bot is not leaking information by saying it back. The phrasing communicates that this is intentional access control, not a malfunction, which matters because real users sometimes find each other's bots and ought to receive a coherent answer.

### Chat history without persistence

Per-chat conversation history is held in a `ConcurrentDictionary<long, ChatHistory>` in `TelegramBotHost`, capped at twenty messages per chat (the system prompt plus the last twenty user/assistant turns). The cap exists to keep token cost predictable on long conversations. The dictionary is lost on process restart, and this is acceptable: Postgres is canonical, the history is conversational glue, and a user who restarts mid-conversation simply re-asks their question. A `/reset` command lets the user clear their own history on demand, which is useful when the model drifts.

A persistent-history design — storing turns in a `chat_log` table keyed by chat id — was considered and deferred. The thesis is not about conversation continuity; it is about agentic decision-making. Persistent history would be the right move for a product that wants to support multi-day conversations across restarts, and is the natural next step if Day 10 were carried beyond the thesis scope.

### What this day demonstrates that the previous nine could not

Every day before today produced a system improvement that lived inside a process the operator had to inspect. Day 10 produces a system that *behaves* in a way the operator can sense: messages arrive on a phone, replies come back to questions typed in natural language, the agent's reasoning becomes a chat history the operator can scroll. The committee will see this layer first during the defense, not the database. The thesis claim — that agentic systems can be built without exotic infrastructure, with narrow tool surfaces and locally readable reasoning, and can be operated through familiar consumer interfaces — becomes demonstrable in a single demonstration, end to end, on a phone.

### Integration-boundary failures: three ways the agent layer leaked, and the fixes

The Telegram query agent (Agent C) was the first component in the system to combine three things at once: a third-party model connector, multi-turn conversation state, and tool calling driven by natural language. Each of these is individually well understood; their intersection surfaced three distinct failures that did not appear anywhere in the preceding nine days. None were failures of the agent's reasoning. All three were failures at the boundary between the abstraction (Semantic Kernel's chat-and-tools API) and the concrete service beneath it (the Gemini REST API and the model's own behaviour). They are recorded here together because the pattern — that the abstraction is sound until the integration boundary is crossed under load — is itself a finding worth defending.

The first failure was structural and total. When the model requested a tool, Semantic Kernel executed it and appended the result to the conversation with the role `function`. The Gemini REST API does not recognise that role; it accepts only `user` and `model`, and rejects any request whose history contains a `function`-role message with HTTP 400 and the message "Role 'function' is not supported." Tool results in the Gemini protocol must instead be sent as a `user`-role message carrying a `functionResponse` part. This is a defect in the connector's Gemini-specific serialisation: the same code path is valid against the OpenAI API, which tolerates the `function` role, and the connector did not specialise it for Gemini. Upgrading the connector to its latest available version did not resolve it. The fix was to stop relying on the connector's automatic tool loop for Agent C and to drive the loop manually: the model is asked for its tool calls with auto-invocation disabled, the calls are executed in application code, and their results are fed back to the model as plain `user`-role text. Gemini therefore never sees a `function` role. Agents A and B retain the connector's automatic invocation, because each completes its work in a single tool round and never round-trips a tool result back to the model, so the defect cannot reach them. The decision to localise the workaround to the one agent that needs it, rather than rewrite all three, is deliberate: it keeps the simpler agents on the simpler code path and confines the complexity to where it is actually required.

The second failure was silent and produced confident wrong answers, which is worse than an error. Asked "how much have I paid this provider this year," the agent consistently returned zero, for every provider and every year. Inspection of the query logs showed the cause was not in the tool or the data but in the model's two blind spots. A language model has no clock: told to use "the current year," it substituted a year from its training distribution — 2024, then 2025 — and queried periods that held no paid bills, returning a correct zero for the wrong question. A direct query confirmed the data was sound: the requested figure existed under the year the model had failed to ask for. The model also approximated entity names, searching for "Телеком" when the stored vendor was "Телекабел," a near-miss that a substring filter cannot rescue. Both were corrected by supplying at runtime what the model cannot know on its own. The current date is injected into the system prompt on each new conversation, so "this year" resolves deterministically. A dedicated resolution tool, `find_vendors`, was added so the agent discovers the exact vendor strings stored in the database — which carry institutional prefixes and Cyrillic city suffixes the user never types — and passes them verbatim to the aggregating tools. The agent is instructed to resolve the vendor before aggregating. This resolve-then-aggregate sequence is itself a small piece of agentic evidence: the agent composes two tools, using the output of the first as the input to the second, rather than executing a single pre-written query. The resolver is data-driven rather than configured: it reads the distinct vendor set live, so a provider whose first bill arrives tomorrow is resolvable immediately, with no code change. Its one acknowledged limit is cross-script matching — a Latin query against a Cyrillic store shares no substring — which is handled by falling back to the full vendor list for the model to choose from, an approach that is correct and cheap at single-user scale and would warrant trigram or transliteration support only at a scale a household will never reach.

The third failure was intermittent and cosmetic but corrosive to trust. On short, context-dependent turns — a bare "yes please" following the agent's own offer to list bills — Gemini occasionally returned an empty candidate: no text and no tool call. The manual loop initially surrendered such turns with the literal string "(no reply)," which a user reasonably reads as the system being broken. The behaviour was non-deterministic, a sampling artefact on the model's side rather than a defect in the application, which is why the identical request succeeded on a second attempt. The fix treats an empty candidate as a transient condition rather than a terminal one: the loop nudges the model once with a brief instruction to answer the previous message, and only after a second empty response falls back — and then to an actionable prompt to rephrase, never to a cryptic token. The principle generalises beyond this instance: a system that places a probabilistic component in front of a user must degrade toward something the user can act on, because the probabilistic component will occasionally produce nothing at all.

Taken together these three findings argue that the difficulty in building such a system lies less in the agent's reasoning, which the preceding days had already shown to be tractable, than in the unglamorous boundary work: making a third-party abstraction speak a specific API correctly, supplying the model the facts it cannot know, and ensuring the system stays usable when the model returns nothing. This is the engineering that does not appear in an architecture diagram and is the larger share of the work.
