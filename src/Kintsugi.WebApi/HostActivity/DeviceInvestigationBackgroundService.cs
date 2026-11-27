using Kintsugi.Application.DeviceInvestigations;
using MediatR;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>Every 15 minutes, investigates the devices that turned Unstable, writes up the ones whose log is in, and
/// sends the alerts (<see cref="RunDeviceInvestigationsCommand"/>). Does nothing until Settings > Device Investigation
/// turns it on. Fifteen minutes is well inside the hour an agent takes to pick a log request up.</summary>
public class DeviceInvestigationBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceInvestigationBackgroundService> _logger;

    public DeviceInvestigationBackgroundService(IServiceScopeFactory scopeFactory, ILogger<DeviceInvestigationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var moved = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RunDeviceInvestigationsCommand(), stoppingToken);
                if (moved > 0)
                {
                    _logger.LogInformation("Device investigations: {Count} opened or reported", moved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Investigating unstable devices failed");
            }
        }
    }
}
