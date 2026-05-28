# bill-agent — 2-week build plan

Tick boxes as you finish. Each day is bite-sized — 1 to 3 hours of focused work. Don't try to do two days in one. Don't beat yourself up if you miss one.

**Started:** 2026-05-22
**Target defense window:** ~2 weeks from start

---

## Week 1 — plumbing & happy path

- [x] **Day 0 — Accounts & credentials** *(boring, do first)*
  - [x] Google Cloud project created, Gmail API + Sheets API enabled
  - [x] OAuth credentials (Desktop app), `credentials.json` downloaded to `./secrets/`
  - [x] Supabase project created, connection string saved
  - [x] Gemini API key in `.env`

- [x] **Day 1 — Gmail read** *(half day for OAuth screens — that's normal)*
  - [x] OAuth handshake working from .NET worker
  - [x] List messages with label "bills" in console
  - [x] Commit: "feat: gmail oauth + label listing"

- [x] **Day 2 — PDF extraction**
  - [x] Download attachment from one labeled message
  - [x] Extract text with PdfPig
  - [x] Print to console
  - [x] Commit: "feat: pdf text extraction"

- [x] **Day 3 — First LLM extraction**
  - [x] Semantic Kernel set up with OpenAI
  - [x] Prompt: given email subject/body/pdf text → JSON {vendor, amount, due_date, period, type, confidence}
  - [x] Try it on one real bill, print JSON
  - [x] Commit: "feat: llm field extraction"

- [x] **Day 4 — Database** *(local Postgres 17 in Docker — see DECISIONS.md for the Supabase pivot)*
  - [x] `docker compose up -d` → Postgres 17 running on localhost:5432
  - [x] `db/migrations/0001_init.sql` applied automatically via docker-entrypoint-initdb.d
  - [x] Verify schema: `docker compose exec postgres psql -U billagent -d billagent -c "\dt"`
  - [x] EF Core 9 + Npgsql provider wired in `Program.cs`
  - [x] BillRepository: HasProcessedAsync (idempotency precheck) + PersistAsync (transactional write)
  - [x] `dotnet run` once → see rows in `email_log`, `bills`, `payments`
  - [x] `dotnet run` AGAIN → all messages skipped (idempotency verified)
  - [x] Commit: "feat: persist bills to local postgres via EF Core (Day 4)"

- [x] **Day 5 — Sheet output** 🎉 *end-of-week-1 milestone*
  - [x] In Google Cloud Console, enable the Sheets API for the existing project
  - [x] Create a fresh Google Sheet (any name); rename Tab 1 to **Bills**
  - [x] Copy the spreadsheet id from its URL → add `BILLAGENT_SHEET_ID=...` to `.env`
  - [x] `dotnet run` → second OAuth browser screen (Sheets scope this time)
  - [x] Verify header row appears in row 1 of "Bills" tab
  - [x] Verify new invoice rows appear (the two from your test inbox)
  - [x] `dotnet run` AGAIN → idempotency holds, sheet does NOT grow (precheck stops at email_log)
  - [x] **MILESTONE: forward a bill → see new sheet row**
  - [x] Commit: "feat: sheets append on new invoice (Day 5)"

## Week 2 — agent loop, reconciliation, polish

- [x] **Day 6 — Payment confirmation path** *(verified — Agent A + BillRepository already handle this end-to-end as of Day 5; no new code required)*
  - [x] Classifier returns `type: "payment_confirmation"` for payment emails (Agent A — verified against 2 test emails)
  - [x] Extract payment fields (vendor, amount, paid_at, references) (Agent A — verified)
  - [x] Store in `payments` table (BillRepository.PersistAsync — verified)
  - [x] `matched_bill_id` correctly left NULL pending Agent B (verified)
  - [x] Commit: "verify: Day 6 payment path already covered by Day 4/5 work" (or roll into Day 7 commit)

- [x] **Day 7 — Reconciler agent** *(the real "agentic" part — see DECISIONS.md "Agent B" entry)*
  - [x] `docker compose up -d` if not already running
  - [x] `dotnet run --project src/BillAgent.Worker` once
  - [x] Watch "Agent B — Reconciler sweep" section in console
  - [x] Expect: BOTH payments matched at confidence ≥ 0.85 (Телекабел and Колекторски both have exact amount + reference match in test data)
  - [x] Verify in DB: `SELECT vendor, status FROM bills;` → both should be 'paid'
  - [x] Verify in DB: `SELECT vendor, matched_bill_id FROM payments;` → both should have matched_bill_id set
  - [x] `dotnet run` AGAIN → idempotency holds at email level AND sweep finds 0 unmatched
  - [x] Commit: "feat: reconciler agent w/ tool use (Day 7)"

- [x] **Day 8 — Sheet upsert** *(orchestrator projects, agent stays at 4 tools — see DECISIONS.md)*
  - [x] `SheetsWriter.UpdateBillStatusAsync` reads col J, writes col G
  - [x] `ReconcilerAgent.ReconcileOneAsync` calls it after each terminal outcome (BillId present)
  - [x] In Google Sheets, add conditional formatting: Status=paid → green, needs_review → yellow
  - [x] `docker compose down -v && docker compose up -d` (fresh DB; Day 7 already painted current bills paid)
  - [x] `dotnet run` → invoice rows appear pending, then Agent B flips them paid in the same run
  - [x] Verify cells turn green via the conditional-formatting rule (no code involved in the colors)
  - [x] Commit: "feat: sheet upsert on status change (Day 8)"

- [ ] **Day 9 — Continuous loop**
  - [ ] `BackgroundService` polls Gmail every 5 min
  - [ ] Idempotency: skip already-processed `gmail_message_id`
  - [ ] Commit: "feat: continuous polling worker"

- [ ] **Day 10 — Telegram bot + Agent C** *(replaces Blazor dashboard — see DECISIONS.md)*
  - [ ] Create bot via @BotFather, store token in secrets
  - [ ] `Telegram.Bot` NuGet + long-polling client
  - [ ] Push notifications from Agent B (new invoice / payment confirmed / needs review)
  - [ ] Agent C: query agent with tools (`list_bills`, `bill_status`, `monthly_summary`, `unpaid_count`, `yearly_total`)
  - [ ] Chat history per user_id so follow-up questions work
  - [ ] Commit: "feat: telegram bot + query agent"

- [ ] **Day 11 — Edge cases**
  - [ ] Vision fallback when PdfPig returns empty text
  - [ ] `needs_review` status when confidence < 0.7
  - [ ] Highlight `needs_review` rows in sheet
  - [ ] Commit: "feat: vision fallback + confidence flagging"

- [ ] **Day 12 — Real-world bug bash**
  - [ ] Forward 20 real bills from your inbox
  - [ ] Fix what breaks (it will)
  - [ ] Commit: "fix: <whatever>"

- [ ] **Day 13 — Buffer** *(don't schedule anything)*

- [ ] **Day 14 — Demo prep**
  - [ ] Dry-run the demo end-to-end
  - [ ] Write "Implementation" section of paper using DECISIONS.md
  - [ ] Prepare 3 bills to forward live during defense

---

## When you're stuck

- Open `DECISIONS.md` — your past self left notes.
- Check the commit log — what was working before?
- If a task feels too big, split it. "Make Gmail OAuth work" → "make the credentials.json load."
- If you're not coding because you don't know where to start, **just open the file you'd edit and read 10 lines.** That's enough to get going.

## Future work (mention in paper, don't build)

- pgvector + RAG for conversational bill queries
- Pub/Sub push instead of polling
- Multi-tenant deployment to Azure Container Apps
- OCR-specific model for scanned bills
