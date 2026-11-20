using MediatR;

namespace Kintsugi.Application.SoftwareTitles.Commands.SetScriptSecret;

/// <summary>Stores a secret's value, creating the secret if it is new, and signs it. The value is never
/// returned by any admin call.</summary>
/// <param name="UpdatedBy">The signed-in administrator, from the session; never from the body.</param>
public record SetScriptSecretCommand(string Name, string Value, string? UpdatedBy = null) : IRequest<SoftwareScreenDto>
{
    /// <summary>Leaves the value out, so a command that ends up in a log line does not carry it there.</summary>
    public override string ToString() => $"{nameof(SetScriptSecretCommand)} {{ Name = {Name}, Value = [redacted], UpdatedBy = {UpdatedBy} }}";
}
