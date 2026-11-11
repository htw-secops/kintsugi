using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.DeviceReport;

public record DeviceSearchResultDto(Guid HostId, string Hostname, string? OperatingSystem, string Platform);

/// <summary>Finds devices by name or serial number, so the report can be opened for a device nobody has flagged yet
/// (a person says their laptop is failing before the health rule has seen enough). Capped, so a fleet of 1,200 returns
/// ten rows, never a list.</summary>
public record SearchDevicesQuery(string Text, int Take) : IRequest<IReadOnlyList<DeviceSearchResultDto>>;

public class SearchDevicesQueryHandler : IRequestHandler<SearchDevicesQuery, IReadOnlyList<DeviceSearchResultDto>>
{
    public const int MaxTake = 20;

    private readonly IHostRepository _hosts;

    public SearchDevicesQueryHandler(IHostRepository hosts) => _hosts = hosts;

    public async Task<IReadOnlyList<DeviceSearchResultDto>> Handle(SearchDevicesQuery request, CancellationToken cancellationToken)
    {
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        // GetAllAsync already leaves out a host whose removal has been requested.
        return (await _hosts.GetAllAsync(cancellationToken))
            .Where(h => h.Hostname.Contains(text, StringComparison.OrdinalIgnoreCase) || h.SerialNumber.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Hostname.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(request.Take, 1, MaxTake))
            .Select(h => new DeviceSearchResultDto(h.Id, h.Hostname, h.OperatingSystem, DevicePlatform.Of(h.OperatingSystem)))
            .ToList();
    }
}
