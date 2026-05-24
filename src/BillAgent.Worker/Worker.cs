using BillAgent.Worker.Services;

namespace BillAgent.Worker;

public class Worker : BackgroundService
{
    private const string Label = "utility-bills";
    private const int MaxMessagesToList = 10;

    private readonly ILogger<Worker> _logger;
    private readonly GmailReader _gmail;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(ILogger<Worker> logger, GmailReader gmail, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _gmail = gmail;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting OAuth handshake with Gmail...");
            await _gmail.InitializeAsync(stoppingToken);

            _logger.LogInformation("Listing messages with label '{Label}'...", Label);
            var messages = await _gmail.ListMessagesByLabelAsync(Label, MaxMessagesToList, stoppingToken);

            _logger.LogInformation("Found {Count} message(s) under label '{Label}':", messages.Count, Label);
            foreach (var m in messages)
            {
                _logger.LogInformation("  - id={Id} threadId={ThreadId}", m.Id, m.ThreadId);
            }

            _logger.LogInformation("Day 1 happy path complete. Shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker failed");
        }
        finally
        {
            // Day 1 is a one-shot. Future days will replace this with a polling loop.
            _lifetime.StopApplication();
        }
    }
}
