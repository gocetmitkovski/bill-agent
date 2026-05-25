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

- [ ] **Day 3 — First LLM extraction**
  - [ ] Semantic Kernel set up with OpenAI
  - [ ] Prompt: given email subject/body/pdf text → JSON {vendor, amount, due_date, period, type, confidence}
  - [ ] Try it on one real bill, print JSON
  - [ ] Commit: "feat: llm field extraction"

- [ ] **Day 4 — Database**
  - [ ] Supabase schema: `bills`, `payments`, `email_log` (see DECISIONS.md)
  - [ ] EF Core or Npgsql wired up
  - [ ] Write extracted bill to `bills` table
  - [ ] Commit: "feat: persist bills to postgres"

- [ ] **Day 5 — Sheet output** 🎉 *end-of-week-1 milestone*
  - [ ] Google Sheets API OAuth
  - [ ] Append new bill row to a target spreadsheet
  - [ ] **MILESTONE: forward a bill → see new sheet row**
  - [ ] Commit: "feat: sheet sync on new bill"

## Week 2 — agent loop, reconciliation, polish

- [ ] **Day 6 — Payment confirmation path**
  - [ ] Classifier returns `type: "confirmation"` for payment emails
  - [ ] Extract payment fields (vendor, amount, paid_at)
  - [ ] Store in `payments` table
  - [ ] Commit: "feat: payment confirmation parsing"

- [ ] **Day 7 — Reconciler agent** *(the real "agentic" part)*
  - [ ] Semantic Kernel function with tools: `query_pending_bills`, `mark_bill_paid`, `flag_for_review`
  - [ ] Given a payment, agent finds the matching pending bill and marks it paid
  - [ ] Commit: "feat: reconciler agent w/ tool use"

- [ ] **Day 8 — Sheet upsert**
  - [ ] Find existing row by `bill_id`, update status cell
  - [ ] Test paid bills turn green in sheet
  - [ ] Commit: "feat: sheet upsert on status change"

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
