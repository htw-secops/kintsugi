using MediatR;

namespace Kintsugi.Application.SoftwareTitles.Commands.DeleteScriptSecret;

/// <summary>Deletes a secret. Refused while any title names it, because that title would stop being
/// served to every agent without saying why.</summary>
public record DeleteScriptSecretCommand(string Name) : IRequest<SoftwareScreenDto>;
