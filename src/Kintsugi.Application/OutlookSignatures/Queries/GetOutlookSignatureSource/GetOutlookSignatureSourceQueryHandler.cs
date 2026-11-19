using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.OutlookSignatures.Queries.GetOutlookSignatureSource;

public class GetOutlookSignatureSourceQueryHandler : IRequestHandler<GetOutlookSignatureSourceQuery, OutlookSignatureSourceDto>
{
    private readonly IOutlookSignatureSourceSettingsRepository _settings;
    private readonly IPersonSignatureRepository _signatures;

    public GetOutlookSignatureSourceQueryHandler(IOutlookSignatureSourceSettingsRepository settings, IPersonSignatureRepository signatures)
    {
        _settings = settings;
        _signatures = signatures;
    }

    public async Task<OutlookSignatureSourceDto> Handle(GetOutlookSignatureSourceQuery request, CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAsync(cancellationToken);
        var copies = await _signatures.CountAsync(cancellationToken);

        return stored is null
            ? new OutlookSignatureSourceDto(false, null, string.Empty, false, null, null, null, null, copies)
            : new OutlookSignatureSourceDto(
                stored.Enabled,
                stored.EndpointUrl,
                stored.WindowsUsernameSuffix,
                stored.IsConfigured,
                stored.LastPullUtc,
                stored.LastPullStatus,
                stored.LastPullCount,
                stored.LastError,
                copies);
    }
}
