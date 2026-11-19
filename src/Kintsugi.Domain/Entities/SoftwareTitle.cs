using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A piece of software Kintsugi should install, offer or remove: a package-manager id (winget,
/// Chocolatey, Homebrew, Flatpak, Snap) or a <em>custom</em> title whose script is an ordinary
/// AI-researched or hand-written upgrade path. What turns it into work on a host is the agent's
/// work list — <c>GetUpgradeStatusesQueryHandler</c> appends an <em>action row</em> for each title
/// that applies to the host and is not yet in the state its <see cref="Mode"/> wants.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One signed script covers the app's whole life.</strong> A title has no script of its own.
/// A package-manager title runs its manager's one signed script, and a custom title runs the
/// upgrade path stored under its own name and OS; both gain <c>--install</c>/<c>--uninstall</c> beside
/// <c>--update-version</c>/<c>--update</c>. Once installed, the app shows up in inventory and is patched
/// like anything else.
/// </para>
/// <para>
/// <strong>The id is signed, not just the script.</strong> One signed winget script would otherwise
/// install whatever id the database names. <see cref="ActionSignature"/> covers
/// <see cref="CanonicalActionText"/> — the action, the manager, the name, the id and the secret names
/// — and is made only when an administrator saves, never at serve time, or it would vouch for whatever
/// the database holds. Every agent recomputes the same text and verifies it with the pinned key.
/// </para>
/// <para>
/// <strong>Assignment is "every host of this OS" or a list of hosts</strong>, the same shape as
/// <see cref="Baseline"/>: host ids rather than names, so a rename does not drop a host.
/// </para>
/// </remarks>
public class SoftwareTitle : BaseEntity
{
    public const int MaxNameLength = 255;
    public const int MaxPackageIdLength = 255;
    public const int MaxDetectAsLength = 255;
    public const int MaxSecretNames = 20;

    public const string InstallAction = "Install";
    public const string UninstallAction = "Uninstall";

    /// <summary>The display name, and the name the action row carries. Use the name Currently
    /// Installed shows for the app, so later upgrade rows resolve to the same name.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>The upgrade-path bucket this title's script lives in: <c>pm:winget</c> and so on for a
    /// package-manager title, <c>Windows</c>/<c>macOS</c>/<c>Linux</c> for a custom one. With
    /// <see cref="Name"/>, the title's unique key.</summary>
    public string Platform { get; private set; } = default!;

    /// <summary>The catalog's name for the manager ("winget"), or null for a custom title.</summary>
    public string? PackageManager { get; private set; }

    /// <summary>The OS bucket a host must be in for this title to apply to it — the manager's own OS
    /// for a package-manager title, <see cref="Platform"/> for a custom one.</summary>
    public string OperatingSystem { get; private set; } = default!;

    /// <summary>The manager's id for the package. Always set for a package-manager title, never for a
    /// custom one, whose <c>--appId</c> is its <see cref="Name"/>.</summary>
    public string? PackageId { get; private set; }

    /// <summary>A name prefix that also counts as installed — "Microsoft 365" for an Office title whose
    /// inventory row carries a language suffix, say.</summary>
    public string? DetectAs { get; private set; }

    public SoftwareMode Mode { get; private set; }

    /// <summary>The <see cref="ScriptSecret"/>s this title's script reads, by name. Each one arrives in
    /// the script's environment as <c>KINTSUGI_SECRET_&lt;NAME&gt;</c>.</summary>
    public List<string> SecretNames { get; private set; } = new();

    /// <summary>Applies to every host whose OS buckets as <see cref="OperatingSystem"/>, in addition to
    /// <see cref="AssignedHostIds"/>.</summary>
    public bool AssignedToAllHosts { get; private set; }

    public List<Guid> AssignedHostIds { get; private set; } = new();

    public string? AssignedBy { get; private set; }

    public string? CreatedBy { get; private set; }

    /// <summary>The server's signature over <see cref="CanonicalActionText"/>, set on every save.</summary>
    public string? ActionSignature { get; private set; }

    private SoftwareTitle()
    {
    }

    /// <param name="platform">The bucket — see <see cref="Platform"/>. The application layer derives it
    /// (and <paramref name="operatingSystem"/>) from <c>PackageManagerCatalog</c>, which the domain cannot
    /// see.</param>
    public static SoftwareTitle Create(
        string name,
        string platform,
        string? packageManager,
        string operatingSystem,
        string? packageId,
        string? detectAs,
        SoftwareMode mode,
        IEnumerable<string> secretNames,
        string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A software title needs a name.");
        }

        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(operatingSystem))
        {
            throw new DomainException("A software title needs a platform.");
        }

        packageManager = Blank(packageManager);
        packageId = Blank(packageId);

        if (packageManager is not null && packageId is null)
        {
            throw new DomainException($"A {packageManager} title needs the package id {packageManager} knows it by.");
        }

        // The contract hands a custom script --appId <title name>, because the uninstall-registry key
        // or bundle id is not known before the install. A package id here would change that silently.
        if (packageManager is null && packageId is not null)
        {
            throw new DomainException("A custom title has no package id; its script finds the app itself. Use 'detected as' to match an inventory name.");
        }

        var title = new SoftwareTitle
        {
            Name = name.Trim(),
            Platform = platform,
            PackageManager = packageManager,
            OperatingSystem = operatingSystem,
            PackageId = packageId,
            CreatedBy = createdBy
        };
        title.Apply(mode, detectAs, secretNames);
        return title;
    }

    /// <summary>Changes what can change after creation. Name, id and source cannot: to change those,
    /// delete the title and add it again. The caller re-signs (see <see cref="SetActionSignature"/>).</summary>
    public void Update(SoftwareMode mode, string? detectAs, IEnumerable<string> secretNames)
    {
        Apply(mode, detectAs, secretNames);
        MarkUpdated();
    }

    /// <summary>Replaces who this title applies to. An empty assignment is allowed: it stops the title
    /// acting anywhere without deleting it.</summary>
    public void Assign(bool allHosts, IEnumerable<Guid> hostIds, string? assignedBy)
    {
        AssignedToAllHosts = allHosts;
        AssignedHostIds = hostIds.Distinct().ToList();
        AssignedBy = assignedBy;
        MarkUpdated();
    }

    public void SetActionSignature(string? signature)
    {
        ActionSignature = signature;
    }

    /// <summary>Prohibited removes; Required and Self-service install.</summary>
    public string Action => Mode == SoftwareMode.Prohibited ? UninstallAction : InstallAction;

    /// <summary>What the script is passed as <c>--appId</c>, and what the action row's
    /// <c>applicationIdentifier</c> carries.</summary>
    public string ApplicationIdentifier => PackageId ?? Name;

    /// <summary>
    /// The exact text <see cref="ActionSignature"/> covers, and that every agent rebuilds from the row
    /// it is served before running it. Fixed by the IP-879 contract; the test vectors in
    /// <c>SoftwareTitleTests</c> and each agent's <c>upgrade.rs</c> pin it.
    /// </summary>
    /// <remarks>Secret names sort ordinally (byte order), which is what Rust's <c>sort</c> does on a
    /// <c>Vec&lt;String&gt;</c>, so both ends agree without a locale.</remarks>
    public string CanonicalActionText() =>
        CanonicalActionTextFor(Action, PackageManager, Name, ApplicationIdentifier, SecretNames);

    public static string CanonicalActionTextFor(
        string action, string? packageManager, string applicationName, string applicationIdentifier, IEnumerable<string> secretNames) =>
        "kintsugi-action-v1\n" + action + "\n" + (packageManager ?? "") + "\n" + applicationName + "\n" + applicationIdentifier + "\n"
        + string.Join(",", secretNames.OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Whether the assignment names this host and the host runs this title's OS.</summary>
    public bool IsAssignedTo(Guid hostId, string hostOsBucket) =>
        string.Equals(hostOsBucket, OperatingSystem, StringComparison.OrdinalIgnoreCase)
        && (AssignedToAllHosts || AssignedHostIds.Contains(hostId));

    /// <summary>Whether the host has this title's package manager — its own inventory row, which every
    /// agent reports under the catalog's name. Always true for a custom title.</summary>
    public bool HasPackageManager(IEnumerable<InstalledApplication> inventory) =>
        PackageManager is null || inventory.Any(a => string.Equals(a.Name, PackageManager, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this title should act on the host at all. A package-manager title on a host without that
    /// manager does not apply — it is not applicable, which is never a failure.
    /// </summary>
    public bool AppliesTo(Guid hostId, string hostOsBucket, IEnumerable<InstalledApplication> inventory) =>
        IsAssignedTo(hostId, hostOsBucket) && HasPackageManager(inventory);

    /// <summary>
    /// Whether the software is on the host: any inventory row whose identifier is the package id, whose
    /// name is the title's name, or whose name starts with <see cref="DetectAs"/>, all ignoring case.
    /// The exact-name match is what guarantees an install row never shares a name with an inventory row.
    /// </summary>
    public bool IsInstalledOn(IEnumerable<InstalledApplication> inventory) => DetectedIn(inventory).Any();

    /// <summary>The inventory rows <see cref="IsInstalledOn"/> matched — the rows a Prohibited title
    /// keeps off the work list.</summary>
    public IEnumerable<InstalledApplication> DetectedIn(IEnumerable<InstalledApplication> inventory) =>
        inventory.Where(Matches);

    private bool Matches(InstalledApplication app) =>
        (PackageId is not null && string.Equals(app.ApplicationIdentifier, PackageId, StringComparison.OrdinalIgnoreCase))
        || string.Equals(app.Name, Name, StringComparison.OrdinalIgnoreCase)
        || (DetectAs is not null && app.Name.StartsWith(DetectAs, StringComparison.OrdinalIgnoreCase));

    private void Apply(SoftwareMode mode, string? detectAs, IEnumerable<string> secretNames)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new DomainException($"'{mode}' is not a software mode.");
        }

        var names = secretNames.Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var invalid = names.Where(n => !ScriptSecret.IsValidName(n)).ToList();
        if (invalid.Count > 0)
        {
            throw new DomainException("Not a valid secret name: " + string.Join(", ", invalid) + ". " + ScriptSecret.NameRule);
        }

        if (names.Count > MaxSecretNames)
        {
            throw new DomainException($"A title can use at most {MaxSecretNames} secrets.");
        }

        Mode = mode;
        DetectAs = Blank(detectAs);
        SecretNames = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
