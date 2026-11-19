using MediatR;

namespace Kintsugi.Application.OutlookSignatures.Queries.GetOutlookSignatureSource;

public record GetOutlookSignatureSourceQuery : IRequest<OutlookSignatureSourceDto>;
