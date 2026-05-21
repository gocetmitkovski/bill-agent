# bill-agent

An agentic system that ingests utility bills from email, extracts structured data with an LLM, and tracks paid/unpaid status in a Google Sheet.

Built as the practical demo for my bachelor thesis on agentic AI systems.

## What it does

1. Watches a Gmail label for forwarded bills and payment confirmations
2. Extracts vendor, amount, due date, and period from PDF attachments and email bodies
3. Stores structured records in Postgres (Supabase)
4. Syncs to a Google Sheet with paid/unpaid status and yearly statistics
5. Reconciles payment confirmations against pending bills (the "agentic" part — uses tools to read state and decide actions)
6. Flags low-confidence cases for human review

## Why this is an agent and not a script

- **Variable input handling.** Each utility provider sends bills in different formats. Instead of hand-coded parsers per vendor, the agent uses an LLM to extract fields and falls back to a vision model when text extraction fails.
- **Tool-using reasoning loop.** The reconciler agent queries the database for pending bills, decides which one a payment matches, and writes the update — using tools, not a fixed pipeline.
- **Human-in-the-loop.** When confidence is low, the agent flags the case rather than guessing.

## Stack

- **.NET 9** + C#
- **Semantic Kernel** for agent orchestration
- **OpenAI GPT-4o-mini** (parsing) + **GPT-4o** (reconciliation, vision fallback)
- **Gmail API** for ingestion
- **Google Sheets API** for output
- **PdfPig** for PDF text extraction
- **Postgres** on Supabase
- **Blazor Server** (one-page dashboard, agent activity log)

See [DECISIONS.md](./DECISIONS.md) for the rationale behind each choice.

## Status

In active development. See the kanban board for current progress.

## License

MIT
