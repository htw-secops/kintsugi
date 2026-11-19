using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The one <see cref="OutlookSignatureSourceSettings"/> row, if an administrator has saved it.</summary>
public interface IOutlookSignatureSourceSettingsRepository
{
    Task<OutlookSignatureSourceSettings?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(OutlookSignatureSourceSettings settings, CancellationToken cancellationToken);
}
