using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The one <see cref="AppearanceSettings"/> row. Mirrors
/// <see cref="IVulnerabilitySettingsRepository"/>.</summary>
public interface IAppearanceSettingsRepository
{
    Task<AppearanceSettings?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(AppearanceSettings settings, CancellationToken cancellationToken);
}
