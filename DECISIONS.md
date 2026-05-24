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
