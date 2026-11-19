using Kintsugi.Application.PatchFailures.Commands.ReverifyPatchFailures;
using MediatR;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>Once a day, re-checks every outstanding patch failure against the last inventory its
/// host sent, closing false alarms. The same check also runs whenever a host reports its
/// applications; this catches hosts that have been quiet.</summary>
public class PatchFailureReconcileBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatchFailureReconcileBackgroundService> _logger;

    public PatchFailureReconcileBackgroundService(IServiceScopeFactory scopeFactory, ILogger<PatchFailureReconcileBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
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
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                var result = await sender.Send(new ReverifyPatchFailuresCommand(null, null), stoppingToken);
                if (result.Resolved > 0)
                {
                    _logger.LogInformation(
                        "Closed {Resolved} of {Checked} outstanding patch failures as false positives", result.Resolved, result.Checked);
                }

                var removed = await sender.Send(new PrunePatchFailuresCommand(), stoppingToken);
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "Removed {Count} settled patch failures older than {Days} days", removed, PrunePatchFailuresCommand.RetentionDays);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Patch failure reconciliation failed");
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
