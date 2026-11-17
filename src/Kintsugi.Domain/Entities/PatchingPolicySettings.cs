using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton fleet-wide policy governing how often patching runs, and — when installing a patch
/// needs an application restart or a host reboot — how long that can be deferred and how many
/// times. The kintsugi-agent is expected to read this (via a future step) and enforce it locally
/// rather than the server pushing patches on a schedule itself.
/// </summary>
public class PatchingPolicySettings : BaseEntity
{
    /// <summary>How often the agent should apply patches — e.g. every 7 <see cref="IntervalUnit"/>.</summary>
    public int IntervalValue { get; private set; }
    public PatchingTimeUnit IntervalUnit { get; private set; }

    /// <summary>How long a single deferral lasts, when a restart/reboot is required and the user
    /// chooses to postpone it — e.g. 24 <see cref="DelayUnit"/>.</summary>
    public int DelayValue { get; private set; }
    public PatchingTimeUnit DelayUnit { get; private set; }

    /// <summary>How many times a restart/reboot can be deferred before the agent must force it
    /// through regardless. Zero means deferral isn't permitted at all.</summary>
    public int MaxDelayCount { get; private set; }

    /// <summary>Longest organisation name a popup will show. The Windows agent lays its popups out
    /// for a line of about this length beside the logo, so the bound is the popup's, not the database's.</summary>
    public const int MaxOrganisationNameLength = 80;

    /// <summary>Largest logo accepted, in bytes. Every agent fetches the policy on every check-in
    /// and the logo rides in that response as base64, so it is kept small enough not to matter.</summary>
    public const int MaxOrganisationLogoBytes = 64 * 1024;

    // The eight-byte signature every PNG file starts with (PNG spec, section 5.2).
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Who the end-user popups say they are from, e.g. "Example Corp IT". Null means the
    /// agent falls back to its own generic wording. Read by the Windows agent from
    /// <c>GET /api/patching-policy</c> as <c>organisationName</c>.</summary>
    public string? OrganisationName { get; private set; }

    /// <summary>PNG shown beside <see cref="OrganisationName"/> on the popups, or null for none.
    /// Crosses the wire as a base64 string (<c>organisationLogoPng</c>) because that is how
    /// System.Text.Json writes a <c>byte[]</c>. Only PNG is accepted, so the agent needs exactly
    /// one decoder and never has to sniff what it was sent.</summary>
    public byte[]? OrganisationLogoPng { get; private set; }

    private PatchingPolicySettings()
    {
    }

    public static PatchingPolicySettings Create(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        var settings = new PatchingPolicySettings();
        settings.Apply(intervalValue, intervalUnit, delayValue, delayUnit, maxDelayCount);
        return settings;
    }

    public void Update(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        Apply(intervalValue, intervalUnit, delayValue, delayUnit, maxDelayCount);
        MarkUpdated();
    }

    /// <summary>Sets what the end-user popups are branded with. Separate from <see cref="Update"/>
    /// because the branding is presentation, not policy: nothing about when or how patching runs
    /// depends on it. A blank name and an empty logo both mean "none".</summary>
    public void UpdateBranding(string? organisationName, byte[]? organisationLogoPng)
    {
        var name = string.IsNullOrWhiteSpace(organisationName) ? null : organisationName.Trim();
        if (name is { Length: > MaxOrganisationNameLength })
        {
            throw new DomainException($"Organisation name cannot be longer than {MaxOrganisationNameLength} characters.");
        }

        var logo = organisationLogoPng is { Length: > 0 } ? organisationLogoPng : null;
        if (logo is { Length: > MaxOrganisationLogoBytes })
        {
            throw new DomainException($"Organisation logo cannot be larger than {MaxOrganisationLogoBytes / 1024} KB.");
        }

        if (logo is not null && !IsPng(logo))
        {
            throw new DomainException("Organisation logo must be a PNG image.");
        }

        OrganisationName = name;
        OrganisationLogoPng = logo;
        MarkUpdated();
    }

    /// <summary>True when <paramref name="bytes"/> starts with the PNG signature. Public so the
    /// command validator can reject a non-PNG as a field error before the domain sees it.</summary>
    public static bool IsPng(byte[] bytes) => bytes.AsSpan().StartsWith(PngSignature);

    private void Apply(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        if (intervalValue < 1)
        {
            throw new DomainException("Patching interval must be at least 1.");
        }

        if (delayValue < 1)
        {
            throw new DomainException("Delay length must be at least 1.");
        }

        if (maxDelayCount < 0)
        {
            throw new DomainException("Maximum number of delays cannot be negative.");
        }

        IntervalValue = intervalValue;
        IntervalUnit = intervalUnit;
        DelayValue = delayValue;
        DelayUnit = delayUnit;
        MaxDelayCount = maxDelayCount;
    }
}
