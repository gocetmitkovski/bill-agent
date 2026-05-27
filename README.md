# bill-agent

An agentic system that ingests utility bills from email, extracts structured data with an LLM, persists them to Postgres, and projects them into a Google Sheet for human review. Built as the practical demo for my bachelor thesis on agentic AI systems.

## What it does

1. Polls a Gmail label (`utility-bills`) for forwarded invoices and payment confirmations
2. Extracts vendor, amount, currency, due date, period, and identifying references from the email body and any PDF attachments
3. Persists structured records to a local Postgres database (single source of truth)
4. Projects each new invoice as a row in a Google Sheet (the human-facing view; a hand-built dashboard tab aggregates totals and colors paid vs. unpaid)
5. Reconciles payment confirmations against pending bills using a tool-using agent (the agentic centerpiece)
6. Flags low-confidence extractions for human review
7. Surfaces notifications and answers conversational queries through a Telegram bot

## Why this is an agent and not a script

- **Variable input handling.** Each utility provider sends bills in different formats and in different languages (the test inbox is Macedonian Cyrillic). Instead of hand-coded parsers per vendor, the extractor agent uses an LLM to read the email and PDF and emit a single normalized record.
- **Tool-using reasoning loop.** The reconciler agent queries the database for pending bills, reasons about which one a given payment matches (vendor + period + amount + reference strings — a fuzzy match no deterministic rule captures cleanly), and writes the update through narrow tools.
- **Conversational query.** A second agent answers questions like "what did I pay this year for vendor X" by selecting from a small set of read tools backed by the database.
- **Human-in-the-loop.** When extraction or reconciliation confidence is low, the agent flags the case rather than guessing.

The architectural commitment is that LLM-driven reasoning is deployed *only* where reasoning is the bottleneck (classification, reconciliation, conversational query). Visualization, layout, aggregation, and persistence stay in deterministic code and standard spreadsheet primitives. See [DECISIONS.md](./DECISIONS.md) for the full design argument.

## Stack

- **.NET 9** + C#
- **Semantic Kernel** for agent orchestration
- **Google Gemini 2.5 Flash** (free tier; vision-native for scanned PDF fallback)
- **Gmail API** for ingestion (read-only OAuth scope)
- **Google Sheets API** for the human-facing projection
- **PdfPig** for PDF text extraction
- **Postgres 17** in Docker — local, offline-capable, deterministic for demo
- **Entity Framework Core 9** + Npgsql provider (DbContext over a hand-written SQL migration)
- **Telegram Bot API** for notifications and the conversational query agent

See [DECISIONS.md](./DECISIONS.md) for the rationale behind each choice — including why we ship local Postgres instead of Supabase, why EF Core with a hand-written schema instead of `dotnet ef migrations`, and why the dashboard is built in Sheets formulas instead of by an agent.

## Run it locally

Prerequisites: .NET 9 SDK, Docker, a Google Cloud project with the Gmail + Sheets APIs enabled, and a Gemini API key.

```bash
# 1. Bring up Postgres (schema applies automatically on first start)
docker compose up -d

# 2. Drop your Google OAuth client into secrets/
#    cp ~/Downloads/client_secret_*.json secrets/google_oauth_client.json

# 3. Create a .env at the repo root with:
#    GEMINI_API_KEY=...
#    BILLAGENT_SHEET_ID=<id from your sheet URL>
#    (BILLAGENT_DB_CONNECTION is optional; default matches docker-compose.yml)

# 4. Run the worker. First run opens two browser tabs (Gmail consent, Sheets consent).
dotnet run --project src/BillAgent.Worker
```

## Repository layout

```
docker-compose.yml             local Postgres 17
db/migrations/0001_init.sql    authoritative schema (bills, payments, email_log)
src/BillAgent.Worker/          the .NET worker
  Data/                          EF entities + DbContext
  Services/                      GmailReader, PdfTextExtractor,
                                 BillExtractor (Agent A),
                                 BillRepository, SheetsWriter
secrets/                       OAuth client + token stores (gitignored)
DECISIONS.md                   running design log; first-draft thesis prose
PLAN.md                        two-week build plan with progress checkboxes
```

## Status

Week 1 (plumbing & happy path) shipped: Gmail ingestion, PDF extraction, LLM field extraction, Postgres persistence with idempotency, Sheets append. Week 2 in progress: payment-confirmation handling, the reconciler agent, continuous polling, and the Telegram bot. See [PLAN.md](./PLAN.md) for day-by-day progress.

## License

MIT
