using System.Text.Json;
using BillAgent.Worker.Services;

namespace BillAgent.Worker;

public class Worker : BackgroundService
{
    private const string Label = "utility-bills";
    private const int MaxMessagesToList = 10;

    private readonly ILogger<Worker> _logger;
    private readonly GmailReader _gmail;
    private readonly PdfTextExtractor _pdf;
    private readonly BillExtractor _extractor;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(
        ILogger<Worker> logger,
        GmailReader gmail,
        PdfTextExtractor pdf,
        BillExtractor extractor,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _gmail = gmail;
        _pdf = pdf;
        _extractor = extractor;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting OAuth handshake with Gmail...");
            await _gmail.InitializeAsync(stoppingToken);

            _logger.LogInformation("Listing messages with label '{Label}'...", Label);
            var stubs = await _gmail.ListMessagesByLabelAsync(Label, MaxMessagesToList, stoppingToken);
            _logger.LogInformation("Found {Count} message(s).", stubs.Count);

            foreach (var stub in stubs)
            {
                var full = await _gmail.GetMessageAsync(stub.Id, stoppingToken);
                var content = GmailReader.ExtractContent(full);

                // Concatenate PDF text from all PDF attachments (most invoices have just one).
                var pdfs = await _gmail.GetPdfAttachmentsAsync(full, stoppingToken);
                string? pdfText = null;
                if (pdfs.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var (filename, bytes) in pdfs)
                        sb.AppendLine(_pdf.Extract(bytes, filename));
                    pdfText = sb.ToString();
                }

                Console.WriteLine();
                Console.WriteLine("════════════════════════════════════════");
                Console.WriteLine($"  Subject: {content.Subject}");
                Console.WriteLine($"  From:    {content.From}");
                Console.WriteLine($"  PDFs:    {pdfs.Count}");
                Console.WriteLine("────────── Agent A says ──────────");

                var extraction = await _extractor.ExtractAsync(content, pdfText, stoppingToken);
                var pretty = JsonSerializer.Serialize(extraction, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Don't \uXXXX-escape Cyrillic / non-ASCII — keep console output human-readable.
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                Console.WriteLine(pretty);
            }

            _logger.LogInformation("Day 3 happy path complete. Shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker failed");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
