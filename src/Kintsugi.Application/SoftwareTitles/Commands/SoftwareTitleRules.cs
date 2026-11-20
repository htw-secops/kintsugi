using FluentValidation;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.SoftwareTitles.Commands;

/// <summary>What more than one software-title command checks the same way.</summary>
internal static class SoftwareTitleRules
{
    /// <summary>Every named secret must already be stored: a title naming one that is not would be served
    /// to no agent, which looks like a title that silently never acts.</summary>
    public static async Task RequireSecretsExistAsync(
        IScriptSecretRepository secrets, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return;
        }

        var stored = (await secrets.GetAllAsync(cancellationToken)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var missing = names.Where(n => !stored.Contains(n.Trim())).ToList();
        if (missing.Count > 0)
        {
            throw new DomainException("No secret is stored as: " + string.Join(", ", missing) + ". Set it in the Secrets panel first.");
        }
    }

    /// <summary>Signs the title's canonical action text. Only ever called from an administrator's save.</summary>
    public static void Sign(SoftwareTitle title, IArtifactSigningService signing) =>
        title.SetActionSignature(signing.Sign(title.CanonicalActionText()));

    public static IRuleBuilderOptions<T, IReadOnlyList<string>> ValidSecretNames<T>(this IRuleBuilder<T, IReadOnlyList<string>> rule) =>
        rule.NotNull()
            .Must(n => n is null || n.Count <= SoftwareTitle.MaxSecretNames)
            .WithMessage($"A title can use at most {SoftwareTitle.MaxSecretNames} secrets.")
            .Must(n => n is null || n.All(ScriptSecret.IsValidName))
            .WithMessage(ScriptSecret.NameRule);
}
