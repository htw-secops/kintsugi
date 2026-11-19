using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Who one signed-in Windows user on one host was last resolved to, for per-person baseline items — and
/// why not, when they were not. Written by <c>POST /api/baseline-people</c> on every call, one row per
/// (host, Windows user), and pruned when not seen for <see cref="RetentionDays"/> days.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The server decides who a session is, never the agent.</strong> The agent reports what it saw —
/// a Windows username and, if the Google Credential Provider recorded one, an email — and this row records
/// what the server made of it. That is also what makes a per-person result trustworthy:
/// <c>ReportBaselineResultsCommandHandler</c> accepts a result for an email only if this table resolved
/// that email for that host within the last day, so an agent cannot report on, or be served, anyone it
/// was not seen with.
/// </para>
/// <para>The admin drill-down (<c>GET /api/admin/baselines/{id}/people</c>) reads these rows directly.</para>
/// </remarks>
public class HostPersonResolution : BaseEntity
{
    public const int RetentionDays = 30;
    public const int MaxWindowsUserLength = 256;
    public const int MaxReasonLength = 300;

    public const string ResolvedByGcpw = "gcpw";
    public const string ResolvedByUsername = "username";

    public Guid HostId { get; private set; }

    /// <summary>As the agent reported it.</summary>
    public string WindowsUser { get; private set; } = default!;

    /// <summary>The person, lower-cased, or null when nobody could be resolved.</summary>
    public string? Email { get; private set; }

    /// <summary><see cref="ResolvedByGcpw"/>, <see cref="ResolvedByUsername"/>, or null with a <see cref="Reason"/>.</summary>
    public string? ResolvedBy { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset LastSeenUtc { get; private set; }

    private HostPersonResolution()
    {
    }

    public static HostPersonResolution Create(Guid hostId, string windowsUser, DateTimeOffset at)
    {
        if (hostId == Guid.Empty || string.IsNullOrWhiteSpace(windowsUser) || windowsUser.Length > MaxWindowsUserLength)
        {
            throw new DomainException("A resolution must name its host and a Windows user.");
        }

        return new HostPersonResolution { HostId = hostId, WindowsUser = windowsUser, LastSeenUtc = at };
    }

    public void Resolved(string email, string resolvedBy, DateTimeOffset at)
    {
        if (resolvedBy is not (ResolvedByGcpw or ResolvedByUsername))
        {
            throw new DomainException($"'{resolvedBy}' is not a way a person is resolved.");
        }

        Email = PersonSignature.NormalizeEmail(email);
        ResolvedBy = resolvedBy;
        Reason = null;
        Seen(at);
    }

    public void Unresolved(string reason, DateTimeOffset at)
    {
        Email = null;
        ResolvedBy = null;
        Reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
        Seen(at);
    }

    private void Seen(DateTimeOffset at)
    {
        LastSeenUtc = at;
        MarkUpdated();
    }
}
