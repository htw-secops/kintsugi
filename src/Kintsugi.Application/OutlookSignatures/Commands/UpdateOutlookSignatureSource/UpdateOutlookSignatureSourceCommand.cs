using MediatR;

namespace Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;

/// <summary>
/// Saves the signature source settings (<c>PUT /api/admin/outlook-signature-source</c>). The pull status
/// fields are the pull's to write and are not part of this.
/// </summary>
/// <param name="EndpointUrl">The source's bulk read. https only: the ID token is sent to it, and minted with
/// this exact string (trimmed) as its audience. There is no separate audience.</param>
/// <param name="WindowsUsernameSuffix">Appended to an email's local part to guess a Windows username when
/// a signed-in person has no Google Credential Provider record. Blank for none.</param>
public record UpdateOutlookSignatureSourceCommand(bool Enabled, string? EndpointUrl, string? WindowsUsernameSuffix)
    : IRequest<OutlookSignatureSourceDto>;
