using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.AppInstaller;

/// <summary>
/// Keeps Kintsugi's copy of App Installer at the latest winget-cli release: a few minutes after start, then every
/// six hours. Six hours because the winget row's script compares a host with the release tag it reads itself, so a
/// copy that lags the release leaves those hosts failed until it catches up; and because a refresh with nothing
/// new is one small API call.
/// </summary>
public class AppInstallerMirrorBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppInstallerMirrorBackgroundService> _logger;

    public AppInstallerMirrorBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AppInstallerMirrorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
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
                var result = await scope.ServiceProvider.GetRequiredService<IAppInstallerMirror>().RefreshAsync(stoppingToken);
                if (result.Changed)
                {
                    _logger.LogInformation("Kintsugi's copy of App Installer is now winget {Version}", result.Offer.Version);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refreshing Kintsugi's copy of App Installer failed; hosts keep the copy already on offer, if any");
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
