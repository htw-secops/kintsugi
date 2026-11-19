using Kintsugi.Application.PatchFailures.Commands.ClassifyPatchFailures;
using MediatR;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>Every few minutes, asks the AI to name the patch failures the text rules could not. It is
/// here, and not in the agent's failure report, so that report stays fast: the agents give up after 15
/// seconds and a model can take 20.</summary>
public class PatchFailureClassifyBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatchFailureClassifyBackgroundService> _logger;

    public PatchFailureClassifyBackgroundService(IServiceScopeFactory scopeFactory, ILogger<PatchFailureClassifyBackgroundService> logger)
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
                var named = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ClassifyPatchFailuresCommand(), stoppingToken);
                if (named > 0)
                {
                    _logger.LogInformation("The AI named {Count} patch failures the text rules could not", named);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Naming patch failures failed");
            }
        }
    }
}
