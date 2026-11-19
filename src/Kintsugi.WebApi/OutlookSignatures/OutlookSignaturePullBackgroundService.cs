using MediatR;
using Kintsugi.Application.OutlookSignatures.Commands.PullOutlookSignatures;

namespace Kintsugi.WebApi.OutlookSignatures;

/// <summary>
/// Pulls the managed Outlook signatures once shortly after start-up and then hourly. Inert until an
/// administrator saves and enables the source: the command returns at once when there is nothing to do.
/// </summary>
/// <remarks>
/// Hourly because agents check in hourly, so a fresher copy would not reach a PC any sooner; and the
/// source renders nightly, so a pull soon after start-up and every hour keeps the window in which an old
/// copy is served to under an hour. A failed pull keeps the copies and is reported on the settings page
/// (<c>LastError</c>), never retried in a tight loop.
/// </remarks>
public class OutlookSignaturePullBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutlookSignaturePullBackgroundService> _logger;

    public OutlookSignaturePullBackgroundService(IServiceScopeFactory scopeFactory, ILogger<OutlookSignaturePullBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Long enough for the database migration and the rest of start-up to finish first.
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PullOutlookSignaturesCommand(), stoppingToken);
                if (result is not null)
                {
                    // The error text is the handler's own wording (status codes and which check failed),
                    // never a token or a signature body.
                    _logger.LogInformation(
                        "Outlook signature pull: {Status}, {Count} copies held{Error}",
                        result.Status, result.Count, result.Error is null ? string.Empty : $" ({result.Error})");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The Outlook signature pull failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
