using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IWindowsPatchBaselineRepository
{
    Task<IReadOnlyList<WindowsPatchBaseline>> GetAllAsync(CancellationToken cancellationToken);
}
