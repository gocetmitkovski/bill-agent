using BillAgent.Worker;
using BillAgent.Worker.Services;
using DotNetEnv;

// Load .env from repo root (walks up from cwd looking for the file).
// Has to happen BEFORE Host.CreateApplicationBuilder so env vars are visible to IConfiguration.
Env.TraversePath().Load();

var builder = Host.CreateApplicationBuilder(args);

// Singleton: we want the same GmailReader instance reused across worker iterations
// so the OAuth handshake + token cache happens only once per process lifetime.
builder.Services.AddSingleton<GmailReader>();
builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddSingleton<BillExtractor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
