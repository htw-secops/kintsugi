using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.OutlookSignatures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines.Commands.ResolveBaselinePeople;

/// <summary>
/// Decides who each signed-in session is, records it, and serves only those people's copies.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Resolution is the server's.</strong> In order: a GCPW email that has a copy resolves as
/// <c>gcpw</c>; otherwise the Windows username is compared, ignoring case, with each copy's
/// <c>localPart + WindowsUsernameSuffix</c> cut to 20 characters (Windows' cap on a local username), and
/// exactly one match resolves as <c>username</c>. None, or several — the cut makes several possible — gives
/// nobody, with the reason. Every outcome is recorded per host and pruned after
/// <see cref="HostPersonResolution.RetentionDays"/> days unseen.
/// </para>
/// <para>
/// <strong>Only copies for this request.</strong> A copy is returned only for an email resolved from a
/// session in this same call, so a certificate gets nobody's signature but its own sessions'.
/// </para>
/// <para>
/// The host's own row for each per-person item is kept here too: removed when somebody with a copy is
/// signed in, so the people's own rows count, and otherwise set to not-applicable, "nobody with a signature
/// signed in". Calling this route at all proves the agent knows the kind, so that also replaces the error an
/// older agent left there.
/// </para>
/// </remarks>
public class ResolveBaselinePeopleCommandHandler : IRequestHandler<ResolveBaselinePeopleCommand, BaselinePeopleDto>
{
    /// <summary>Windows' cap on a local username, which a mapped name is cut to before comparing.</summary>
    public const int WindowsUsernameMaxLength = 20;

    public const string NobodySignedIn = "nobody with a signature signed in";

    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _applications;
    private readonly IPersonSignatureRepository _signatures;
    private readonly IOutlookSignatureSourceSettingsRepository _sourceSettings;
    private readonly IUnitOfWork _unitOfWork;

    public ResolveBaselinePeopleCommandHandler(
        IBaselineRepository baselines,
        IHostRepository hosts,
        IInstalledApplicationRepository applications,
        IPersonSignatureRepository signatures,
        IOutlookSignatureSourceSettingsRepository sourceSettings,
        IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _hosts = hosts;
        _applications = applications;
        _signatures = signatures;
        _sourceSettings = sourceSettings;
        _unitOfWork = unitOfWork;
    }

    public async Task<BaselinePeopleDto> Handle(ResolveBaselinePeopleCommand request, CancellationToken cancellationToken)
    {
        var none = new BaselinePeopleDto(Array.Empty<BaselinePersonDto>());

        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (host is null || host.DeletedAtUtc is not null)
        {
            return none;
        }

        var assigned = BaselineCompliance.AssignedTo(
            await _baselines.GetAllAsync(cancellationToken), host, await _applications.GetNamesForHostAsync(host.Id, cancellationToken));
        var personItems = (await _baselines.GetServedVersionsAsync(assigned.Select(b => b.Id).ToList(), cancellationToken))
            .SelectMany(v => BaselineDocument.Parse(v.Document).Items.Where(i => i.IsPerPerson).Select(i => (VersionId: v.Id, i.Key)))
            .ToList();
        if (personItems.Count == 0)
        {
            return none;
        }

        var now = DateTimeOffset.UtcNow;
        var suffix = (await _sourceSettings.GetAsync(cancellationToken))?.WindowsUsernameSuffix ?? string.Empty;
        var withCopy = (await _signatures.GetEmailsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var resolutions = (await _baselines.GetResolutionsAsync(host.Id, cancellationToken))
            .ToDictionary(r => r.WindowsUser, StringComparer.OrdinalIgnoreCase);

        var sessions = request.Sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.WindowsUser))
            .DistinctBy(s => s.WindowsUser.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolved = new List<HostPersonResolution>();
        foreach (var session in sessions)
        {
            var windowsUser = session.WindowsUser.Trim();
            if (!resolutions.TryGetValue(windowsUser, out var row))
            {
                row = HostPersonResolution.Create(host.Id, windowsUser, now);
                resolutions[windowsUser] = row;
                await _baselines.AddResolutionAsync(row, cancellationToken);
            }

            var (email, by, reason) = Resolve(windowsUser, session.GcpwEmail, withCopy, suffix);
            if (email is not null)
            {
                row.Resolved(email, by!, now);
            }
            else
            {
                row.Unresolved(reason!, now);
            }

            resolved.Add(row);
        }

        // Never the rows just touched: a person back after more than 30 days is still stale in the database
        // until this call saves, and pruning them here would drop the resolution their results are checked against.
        await _baselines.PruneResolutionsAsync(
            now.AddDays(-HostPersonResolution.RetentionDays), resolved.Select(r => r.Id).ToList(), cancellationToken);

        var emails = resolved.Where(r => r.Email is not null).Select(r => r.Email!).ToHashSet(StringComparer.Ordinal);
        var copies = (await _signatures.GetByEmailsAsync(emails, cancellationToken)).ToDictionary(c => c.Email, StringComparer.Ordinal);
        var versionIds = personItems.Select(p => p.VersionId).Distinct().ToList();
        var personKeys = personItems.ToHashSet();
        var states = (await _baselines.GetHostStatesAsync(host.Id, versionIds, cancellationToken))
            .Where(s => personKeys.Contains((s.BaselineVersionId, s.ItemKey)))
            .ToList();

        await KeepHostRows(host.Id, personItems, states, anyoneWithCopy: emails.Count > 0, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BaselinePeopleDto(resolved
            .Select(r => new BaselinePersonDto(
                r.WindowsUser,
                r.Email,
                r.ResolvedBy,
                r.Reason,
                r.Email is not null && copies.TryGetValue(r.Email, out var copy)
                    ? new PersonSignatureCopyDto(copy.Html, copy.Text, copy.Sha256, copy.RenderedAtUtc)
                    : null,
                r.Email is null
                    ? Array.Empty<PersonItemStateDto>()
                    : states
                        .Where(s => s.Subject == r.Email)
                        .Select(s => new PersonItemStateDto(s.BaselineVersionId, s.ItemKey, s.State.ToString(), s.ObservedValue))
                        .ToList()))
            .ToList());
    }

    /// <summary>Who <paramref name="windowsUser"/> is, or why nobody: see the class remarks for the order.</summary>
    public static (string? Email, string? ResolvedBy, string? Reason) Resolve(
        string windowsUser, string? gcpwEmail, IReadOnlySet<string> withCopy, string suffix)
    {
        string? gcpwWithoutCopy = null;
        if (!string.IsNullOrWhiteSpace(gcpwEmail) && OutlookSignatureRules.IsValidEmail(gcpwEmail))
        {
            var gcpw = PersonSignature.NormalizeEmail(gcpwEmail);
            if (withCopy.Contains(gcpw))
            {
                return (gcpw, HostPersonResolution.ResolvedByGcpw, null);
            }

            gcpwWithoutCopy = gcpw;
        }

        var matches = withCopy.Where(email => string.Equals(MappedUsername(email, suffix), windowsUser, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return (matches[0], HostPersonResolution.ResolvedByUsername, null);
        }

        var because = gcpwWithoutCopy is null ? string.Empty : "the signed-in Google account has no signature, and ";
        return (null, null, matches.Count == 0
            ? because + "no person with a signature matches this Windows username"
            : because + $"{matches.Count} people with a signature match this Windows username once cut to {WindowsUsernameMaxLength} characters, so none is chosen");
    }

    /// <summary><c>localPart + suffix</c>, cut to <see cref="WindowsUsernameMaxLength"/>.</summary>
    public static string MappedUsername(string email, string suffix)
    {
        var at = email.IndexOf('@');
        var mapped = (at < 0 ? email : email[..at]) + suffix;
        return mapped.Length > WindowsUsernameMaxLength ? mapped[..WindowsUsernameMaxLength] : mapped;
    }

    private async Task KeepHostRows(
        Guid hostId,
        IReadOnlyList<(Guid VersionId, string Key)> personItems,
        IReadOnlyList<HostBaselineItemState> states,
        bool anyoneWithCopy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var (versionId, key) in personItems)
        {
            var own = states.FirstOrDefault(s =>
                s.BaselineVersionId == versionId && s.ItemKey == key && s.Subject == HostBaselineItemState.MachineSubject);

            if (anyoneWithCopy)
            {
                if (own is not null)
                {
                    _baselines.RemoveStates(new[] { own });
                }

                continue;
            }

            if (own is null)
            {
                own = HostBaselineItemState.Create(hostId, versionId, key, now);
                await _baselines.AddStateAsync(own, cancellationToken);
            }

            own.Record(BaselineItemState.NotApplicable, null, NobodySignedIn, now);
        }
    }
}
