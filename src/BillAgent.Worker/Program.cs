using BillAgent.Worker;
using BillAgent.Worker.Data;
using BillAgent.Worker.Services;
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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
