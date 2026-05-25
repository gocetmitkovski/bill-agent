using BillAgent.Worker;
using BillAgent.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Singleton: we want the same GmailReader instance reused across worker iterations
// so the OAuth handshake + token cache happens only once per process lifetime.
builder.Services.AddSingleton<GmailReader>();
builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
