using Kintsugi.Application.PatchFailures.Commands.DraftPatchFailureRepairs;
using MediatR;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>Every half hour, drafts a repaired script for AI-written scripts that keep failing on
/// hosts (<see cref="DraftPatchFailureRepairsCommand"/>). It drafts and never signs: the signed script
/// keeps running until somebody reviews the draft on the Failed Updates screen and signs it.</summary>
public class PatchFailureRepairBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatchFailureRepairBackgroundService> _logger;

    public PatchFailureRepairBackgroundService(IServiceScopeFactory scopeFactory, ILogger<PatchFailureRepairBackgroundService> logger)
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
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new DraftPatchFailureRepairsCommand(), stoppingToken);
                if (result.Drafted > 0)
                {
                    _logger.LogInformation("Drafted {Count} script repairs for failing patches; they wait for a human to sign them", result.Drafted);
                }

                foreach (var note in result.Notes)
                {
                    _logger.LogInformation("No repair drafted: {Note}", note);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Drafting script repairs failed");
            }
        }
    }
}
