using BillAgent.Worker;
using BillAgent.Worker.Data;
using BillAgent.Worker.Services;
using BillAgent.Worker.Services.Reconciler;
using BillAgent.Worker.Services.Telegram;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;

// Load .env from repo root (walks up from cwd looking for the file).
// Has to happen BEFORE Host.CreateApplicationBuilder so env vars are visible to IConfiguration.
Env.TraversePath().Load();

var builder = Host.CreateApplicationBuilder(args);

// ── Postgres / EF Core ─────────────────────────────────────────────────────
// Connection string from .env (BILLAGENT_DB_CONNECTION). Default matches docker-compose.yml
// so the dev loop is `docker compose up -d && dotnet run`.
var connectionString = Environment.GetEnvironmentVariable("BILLAGENT_DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=billagent;Username=billagent;Password=billagent";

builder.Services.AddDbContext<BillAgentDbContext>(opts =>
    opts.UseNpgsql(connectionString));

// Singleton: we want the same GmailReader instance reused across worker iterations
// so the OAuth handshake + token cache happens only once per process lifetime.
builder.Services.AddSingleton<GmailReader>();
builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddSingleton<BillExtractor>();
builder.Services.AddSingleton<SheetsWriter>();
// BillRepository is singleton too — it opens a DbContext scope per call internally,
// which is the standard pattern for using EF from a singleton hosted service.
builder.Services.AddSingleton<BillRepository>();
// Agent B (the Reconciler). Singleton because it carries no per-request state;
// each ReconcileOneAsync opens its own DbContext scope and builds a fresh Kernel.
builder.Services.AddSingleton<ReconcilerAgent>();

// ── Day 10: Telegram bot + Agent C ─────────────────────────────────────────
// Notifier is the push side (used by Worker + ReconcilerAgent).
// QueryAgent is Agent C — the read-only chat agent.
// BotHost is a SECOND BackgroundService that long-polls Telegram for inbound
// messages. The main Worker handles ingest/reconcile; the BotHost handles user
// chat. They run in the same process but don't share a polling loop.
builder.Services.AddSingleton<TelegramWhitelist>();
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddSingleton<QueryAgent>();
builder.Services.AddHostedService<TelegramBotHost>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
