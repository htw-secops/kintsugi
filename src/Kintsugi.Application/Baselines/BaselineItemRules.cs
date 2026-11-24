using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;

namespace Kintsugi.Application.Baselines;

/// <summary>
/// What a valid baseline item is, checked before a version is created — and therefore before anybody
/// can sign one. The Windows agent (<c>baseline.rs</c>) accepts the same spellings and reports anything
/// else as an error rather than guessing.
/// </summary>
public static class BaselineItemRules
{
    public const string KindSetting = "setting";
    public const string KindFact = "fact";
    public const string KindOutlookSignature = "outlook-signature";
    public const string KindGoogleMdm = "google-mdm";
    public const string ScopeMachine = "machine";
    public const string ScopeUser = "user";
    public const string HiveMachine = "HKLM";
    public const string HiveUser = "HKCU";
    public const string ModeAudit = "audit";
    public const string ModeEnforce = "enforce";

    /// <summary>
    /// The only registry values a baseline may <c>enforce</c>, each named exactly (path, value name and
    /// type) with the data it may be set to. Everything else can be audited, never written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list rather than a rule because the agent writes as SYSTEM, and the wrong value is code
    /// execution (Run, a service's ImagePath, Image File Execution Options, Winlogon, a COM server's
    /// InprocServer32) or quieter harm even under <c>SOFTWARE\Policies</c> (an extension force-install
    /// list, a WSUS server, Defender switched off). Signing decides what a baseline says; this list
    /// decides what any signed baseline can ever write, so a stolen signing key cannot reach further.
    /// Widening it is a reviewed change here and in the agent's copy (<c>baseline::ENFORCEABLE</c>),
    /// never a signature; <c>BaselineEnforceTests</c> holds the two copies together.
    /// </para>
    /// <para>
    /// The range is part of the bound, because a security setting's weakest value is usually its "off":
    /// <c>InactivityTimeoutSecs</c> 0 removes the idle lock, and a password-on-wake index 0 stops asking.
    /// A name alone would let a signed document turn either off and report it compliant. So every entry
    /// says the lowest and highest data it may be set to, and anything outside is refused here and again
    /// by the agent. Every entry is a dword today; one of another type would need its own rule, and
    /// until it has one <see cref="EnforceableValue.Allows"/> refuses it.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<EnforceableValue> EnforceableValues = new[]
    {
        // Master Daily section 17, the enforce pilot: Chrome keeps its file and folder pickers open.
        // Either value is harmless, so the range is the whole flag.
        new EnforceableValue(@"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "dword", 0, 1,
            "Chrome window occlusion detection", ""),

        // IP-926: "Interactive logon: Machine inactivity limit", the idle screen lock that works on a PC
        // signed in through GCPW (the DeviceLock CSP does not). Windows takes 0 to 599940, and 0 turns
        // the lock off. The floor stops a document locking every screen within seconds of the last
        // keystroke; the ceiling is CIS's and Microsoft's security baseline ("900 or fewer, but not 0"),
        // because a lock that waits hours is the lock turned off by another name.
        new EnforceableValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", "dword", 60, 900,
            "Interactive logon: Machine inactivity limit", "seconds"),

        // IP-926: "Require a password when a computer wakes", plugged in and on battery. The GUID is
        // CONSOLELOCK's; Master Daily section 9 wrote one that does not exist. 1 asks for a password and
        // 0 does not, so 1 is the only value enforce may write.
        new EnforceableValue(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51", "ACSettingIndex", "dword", 1, 1,
            "Require a password on wake (plugged in)", ""),
        new EnforceableValue(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51", "DCSettingIndex", "dword", 1, 1,
            "Require a password on wake (on battery)", ""),
    };

    /// <summary>The <see cref="EnforceableValues"/> entry <paramref name="item"/> names, whatever its data,
    /// or null when it names none.</summary>
    public static EnforceableValue? FindEnforceable(BaselineItem item) =>
        item.Kind == KindSetting && item.Scope == ScopeMachine && item.Hive == HiveMachine
            ? EnforceableValues.FirstOrDefault(v =>
                string.Equals(v.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.ValueName, item.ValueName, StringComparison.OrdinalIgnoreCase)
                && v.ValueType == item.ValueType)
            : null;

    /// <summary>Whether <paramref name="item"/> names a value on <see cref="EnforceableValues"/> and asks for
    /// data in its range.</summary>
    public static bool IsEnforceable(BaselineItem item) => FindEnforceable(item)?.Allows(item.Data) == true;

    /// <summary>The one key path a <c>google-mdm</c> item names: where Windows lists its MDM enrolments. Fixed,
    /// because what the item reads and its one action are the agent's (<c>google_mdm.rs</c>), not the
    /// document's to choose.</summary>
    public const string GoogleMdmPath = @"SOFTWARE\Microsoft\Enrollments";

    /// <summary>
    /// A <c>google-mdm</c> item's data: how many days the live Google enrolment may go without a successful
    /// sync before it counts as dead. A whole number in this range.
    /// </summary>
    /// <remarks>
    /// The floor keeps a laptop that was off for a long weekend from counting as dead. The ceiling keeps a
    /// dead enrolment from going unnoticed for a quarter. An enrolment made by a Google account that has since
    /// been deleted fails every sync from the day the account goes (IP-925: a year of failures on one laptop),
    /// so anything in this range finds it.
    /// </remarks>
    public const int GoogleMdmMinStaleDays = 3;
    public const int GoogleMdmMaxStaleDays = 90;

    public static bool IsValidStaleDays(string? data) =>
        data is not null
        && data.Length <= 2
        && int.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
        && days is >= GoogleMdmMinStaleDays and <= GoogleMdmMaxStaleDays;

    /// <summary>The one key path an <c>outlook-signature</c> item names: Outlook 2016 and later's own key under
    /// the person's HKCU. It is fixed because the item's real targets are <see cref="SignatureWrites"/>, which
    /// sit under it and under Office's common mail settings, and nothing about them is the document's to choose.</summary>
    public const string OutlookSignaturePath = @"Software\Microsoft\Office\16.0\Outlook";

    /// <summary>A signature name is a Windows file name, and the data of four registry values: 1–64 letters,
    /// digits, spaces and <c>( ) . _ -</c>, starting with a letter or digit. A trailing dot or space is refused
    /// separately (<see cref="IsValidSignatureName"/>), because Windows strips both from a file name and the
    /// registry would then name a file that does not exist.</summary>
    public static readonly Regex SignatureNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 ()._-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValidSignatureName(string? name) =>
        name is not null && SignatureNamePattern.IsMatch(name) && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>
    /// Everything an <c>outlook-signature</c> item can ever cause the agent to write, for any document. The
    /// agent compiles in the same list (<c>baseline::SIGNATURE_WRITES</c>, part 4 of IP-871) and
    /// <c>BaselineSignatureWritesTests</c> holds the two together, as <c>BaselineEnforceTests</c> does for
    /// <see cref="EnforceableValues"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixed list for the reason <see cref="EnforceableValues"/> is one: signing decides what a document says,
    /// this decides what any signed document can reach. The placeholders are filled by the agent, never by the
    /// document — <c>&lt;RoamingAppData&gt;</c> from the person's own profile, <c>&lt;profile&gt;</c> and
    /// <c>&lt;account&gt;</c> from the Outlook profiles it finds under that person's HKCU (an account key is
    /// eight hex digits), and <c>&lt;signatureName&gt;</c> from the item, which <see cref="IsValidSignatureName"/>
    /// has already confined to a bare file name. Every value written holds the signature name and nothing else.
    /// </para>
    /// <para>
    /// The agent writes these under the person's own token, not as SYSTEM, so a junction or hard link planted
    /// in the profile only ever reaches what that person could already write. Widening this list is a
    /// reviewed change in both places, never a signature.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<SignatureWrite> SignatureWrites = new[]
    {
        new SignatureWrite(SignatureWrite.TargetFile, @"<RoamingAppData>\Microsoft\Signatures\<signatureName>.htm", "", ""),
        new SignatureWrite(SignatureWrite.TargetFile, @"<RoamingAppData>\Microsoft\Signatures\<signatureName>.txt", "", ""),
        new SignatureWrite(SignatureWrite.TargetHkcu, @"Software\Microsoft\Office\16.0\Outlook\Profiles\<profile>\9375CFF0413111d3B88A00104B2A6676\<account>", "New Signature", "string"),
        new SignatureWrite(SignatureWrite.TargetHkcu, @"Software\Microsoft\Office\16.0\Outlook\Profiles\<profile>\9375CFF0413111d3B88A00104B2A6676\<account>", "Reply-Forward Signature", "string"),
        new SignatureWrite(SignatureWrite.TargetHkcu, @"Software\Microsoft\Office\16.0\Common\MailSettings", "NewSignature", "expandString"),
        new SignatureWrite(SignatureWrite.TargetHkcu, @"Software\Microsoft\Office\16.0\Common\MailSettings", "ReplySignature", "expandString"),
    };

    public static readonly IReadOnlyList<string> ValueTypes = new[] { "dword", "qword", "string", "expandString", "multiString", "binary" };

    /// <summary>The most items one version may hold: far above Master Daily's 42, and what stops a
    /// hostile body creating an unbounded document.</summary>
    public const int MaxItems = 500;

    public const int MaxKeyLength = 100;
    public const int MaxPathLength = 512;
    public const int MaxValueNameLength = 255;
    public const int MaxDataLength = 2048;
    public const int MaxReasonLength = 1000;

    /// <summary>
    /// HKLM roots no baseline may read: <c>SAM</c> holds the local accounts' password hashes and
    /// <c>SECURITY</c> the LSA secrets. The agent reads as SYSTEM, which can open both, and whatever it
    /// read would be reported to this server, shown in the admin UI and cached in a Users-readable file.
    /// Checked again by the Windows agent before any read (<c>baseline::is_protected_path</c>), so a
    /// document that reached it some other way is refused there too.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedRoots = new[] { "SAM", "SECURITY" };

    /// <summary>Whether <paramref name="path"/>'s first segment is one of <see cref="ProtectedRoots"/>.</summary>
    public static bool IsProtectedPath(string? path) =>
        path is not null
        && ProtectedRoots.Contains(path.TrimStart('\\').Split('\\')[0], StringComparer.OrdinalIgnoreCase);

    public static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
    public static readonly Regex SecretPattern = new("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HexPattern = new("^([0-9a-f]{2})*$", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="data"/> is well-formed for <paramref name="valueType"/>.</summary>
    public static bool DataFits(string valueType, string data) => valueType switch
    {
        "dword" => uint.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out _),
        "qword" => ulong.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out _),
        "binary" => HexPattern.IsMatch(data),
        _ => true
    };
}

/// <summary>One registry value a baseline may enforce (<see cref="BaselineItemRules.EnforceableValues"/>), and
/// the data it may be set to.</summary>
/// <param name="Min">The lowest data enforce may write, inclusive.</param>
/// <param name="Max">The highest data enforce may write, inclusive.</param>
/// <param name="Title">What the value is called where an administrator meets it (Group Policy's name for it,
/// say), for messages. Not part of what the agent compiles in.</param>
/// <param name="Unit">What the number counts, for messages ("seconds"); empty for a flag or an index.</param>
public record EnforceableValue(string Path, string ValueName, string ValueType, ulong Min, ulong Max, string Title, string Unit)
{
    /// <summary>Whether <paramref name="data"/> is a plain decimal within <see cref="Min"/> to <see cref="Max"/>.
    /// Anything else is refused, so an entry of a type with no range rule can never be enforced.</summary>
    public bool Allows(string? data) =>
        ValueType is "dword" or "qword"
        && ulong.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= Min && value <= Max;

    /// <summary>The range in words, to follow "can be enforced only": "from 60 to 900 seconds", or "at 1".</summary>
    public string Range => (Min == Max ? $"at {Min}" : $"from {Min} to {Max}") + (Unit.Length > 0 ? $" {Unit}" : "");
}

/// <summary>One target an <c>outlook-signature</c> item may write (<see cref="BaselineItemRules.SignatureWrites"/>).</summary>
/// <param name="Target"><see cref="TargetFile"/> or <see cref="TargetHkcu"/>.</param>
/// <param name="Path">A file path or an HKCU key path, with the placeholders the agent fills.</param>
/// <param name="ValueName">The registry value's name; empty for a file.</param>
/// <param name="ValueType">The registry value's type, in the document's spelling; empty for a file.</param>
public record SignatureWrite(string Target, string Path, string ValueName, string ValueType)
{
    public const string TargetFile = "file";
    public const string TargetHkcu = "HKCU";
}

public class BaselineItemValidator : AbstractValidator<BaselineItem>
{
    public BaselineItemValidator()
    {
        RuleFor(i => i.Key).NotEmpty().MaximumLength(BaselineItemRules.MaxKeyLength)
            .Matches(BaselineItemRules.KeyPattern).WithMessage("An item key is lower-case letters, digits, '.', '_' and '-'.");
        RuleFor(i => i.Kind).Must(k => k is BaselineItemRules.KindSetting or BaselineItemRules.KindFact
                or BaselineItemRules.KindOutlookSignature or BaselineItemRules.KindGoogleMdm)
            .WithMessage("An item's kind is 'setting', 'fact', 'outlook-signature' or 'google-mdm'.");
        RuleFor(i => i.SignatureName).Null().When(i => i.Kind != BaselineItemRules.KindOutlookSignature)
            .WithMessage("Only an outlook-signature item names a signature.");
        RuleFor(i => i.Scope).Must(s => s is BaselineItemRules.ScopeMachine or BaselineItemRules.ScopeUser)
            .WithMessage("An item's scope is 'machine' or 'user'.");
        RuleFor(i => i.Hive)
            .Must((item, hive) => hive == (item.Scope == BaselineItemRules.ScopeUser ? BaselineItemRules.HiveUser : BaselineItemRules.HiveMachine))
            .WithMessage("A machine item is under HKLM and a user item under HKCU.");
        RuleFor(i => i.Path).NotEmpty().MaximumLength(BaselineItemRules.MaxPathLength)
            .Must(p => p is not null && !p.StartsWith('\\') && !p.EndsWith('\\') && !p.Contains(@"\\") && !p.Contains('/'))
            .WithMessage("A key path is backslash-separated with no leading, trailing or doubled backslash.")
            .Must(p => !BaselineItemRules.IsProtectedPath(p))
            .WithMessage("SAM and SECURITY hold password hashes and LSA secrets, and no baseline may read them.");
        RuleFor(i => i.ValueName).NotNull().MaximumLength(BaselineItemRules.MaxValueNameLength);
        RuleFor(i => i.Reason).MaximumLength(BaselineItemRules.MaxReasonLength);

        When(i => i.Kind == BaselineItemRules.KindSetting, () =>
        {
            RuleFor(i => i.ValueType).Must(t => t is not null && BaselineItemRules.ValueTypes.Contains(t))
                .WithMessage("A setting's valueType is one of: " + string.Join(", ", BaselineItemRules.ValueTypes) + ".");
            RuleFor(i => i.Mode).Must(m => m is BaselineItemRules.ModeAudit or BaselineItemRules.ModeEnforce)
                .WithMessage("A setting's mode is 'audit' or 'enforce'.");
            RuleFor(i => i).Must(i => BaselineItemRules.FindEnforceable(i) is not null).When(i => i.Mode == BaselineItemRules.ModeEnforce)
                .WithName("Mode")
                .WithMessage(i => $@"Only the values in BaselineItemRules.EnforceableValues can be enforced, and {i.Hive}\{i.Path}\{i.ValueName} ({i.ValueType}) is not one of them. Audit it, or add it to that list in a reviewed change.");
            // On the list, but asking for data outside its range: most often the value's "off" (see EnforceableValues).
            RuleFor(i => i).Must(BaselineItemRules.IsEnforceable)
                .When(i => i.Mode == BaselineItemRules.ModeEnforce && i.Secret is null && BaselineItemRules.FindEnforceable(i) is not null)
                .WithName("Data")
                .WithMessage(i => $"{BaselineItemRules.FindEnforceable(i)!.Title} can be enforced only {BaselineItemRules.FindEnforceable(i)!.Range}, and this item asks for '{i.Data}'. Audit it, or change the range in BaselineItemRules.EnforceableValues in a reviewed change.");
            // The agent never receives a secret's value in this step, so it has nothing to write.
            RuleFor(i => i.Secret).Null().When(i => i.Mode == BaselineItemRules.ModeEnforce)
                .WithMessage("A setting whose value is a secret cannot be enforced yet: agents do not receive secrets.");
            RuleFor(i => i.Secret).Matches(BaselineItemRules.SecretPattern).When(i => i.Secret is not null)
                .WithMessage("A secret is named in UPPER_SNAKE_CASE.");
            RuleFor(i => i.Data).Null().When(i => i.Secret is not null)
                .WithMessage("A setting whose value is a secret names the secret and carries no data.");
            RuleFor(i => i.Data).NotNull().MaximumLength(BaselineItemRules.MaxDataLength).When(i => i.Secret is null)
                .WithMessage($"A setting needs its expected data, at most {BaselineItemRules.MaxDataLength} characters.");
            RuleFor(i => i).Must(i => i.Data is null || i.ValueType is null || BaselineItemRules.DataFits(i.ValueType, i.Data))
                .WithName("Data")
                .WithMessage(i => $"'{i.Data}' is not valid {i.ValueType} data (decimal for dword/qword, lower-case hex for binary).");
        });

        // The item names its policy — on, under this name, the default for new mail and replies — and nothing
        // else: what it writes is BaselineItemRules.SignatureWrites, and the HTML is each person's copy, served
        // per person over mTLS (POST /api/baseline-people) rather than signed into the document.
        When(i => i.Kind == BaselineItemRules.KindOutlookSignature, () =>
        {
            RuleFor(i => i.Scope).Equal(BaselineItemRules.ScopeUser).WithMessage("An outlook-signature item is user scope.");
            RuleFor(i => i.Path).Equal(BaselineItemRules.OutlookSignaturePath)
                .WithMessage($@"An outlook-signature item's path is exactly {BaselineItemRules.OutlookSignaturePath}.");
            RuleFor(i => i.ValueName).Equal(string.Empty).WithMessage("An outlook-signature item names no value.");
            RuleFor(i => i.ValueType).Null().WithMessage("An outlook-signature item has no valueType: its writes are fixed.");
            RuleFor(i => i.Data).Null().WithMessage("An outlook-signature item carries no data: each person's copy is served separately.");
            RuleFor(i => i.Secret).Null().WithMessage("An outlook-signature item has no secret.");
            // Enforce is allowed for this kind. It is bounded by SignatureWrites, not by EnforceableValues.
            RuleFor(i => i.Mode).Must(m => m is BaselineItemRules.ModeAudit or BaselineItemRules.ModeEnforce)
                .WithMessage("An outlook-signature item's mode is 'audit' or 'enforce'.");
            RuleFor(i => i.SignatureName).Must(BaselineItemRules.IsValidSignatureName)
                .WithMessage("A signature name is 1–64 letters, digits, spaces and ( ) . _ -, starting with a letter or digit and not ending with a dot or space.");
        });

        // The item names its policy — check, or also act — and how stale is dead. What it reads, and the one
        // thing enforce can do (unregister the live Google enrolment), are fixed in the agent: see google_mdm.rs.
        When(i => i.Kind == BaselineItemRules.KindGoogleMdm, () =>
        {
            RuleFor(i => i.Scope).Equal(BaselineItemRules.ScopeMachine).WithMessage("A google-mdm item is machine scope.");
            RuleFor(i => i.Path).Equal(BaselineItemRules.GoogleMdmPath)
                .WithMessage($@"A google-mdm item's path is exactly {BaselineItemRules.GoogleMdmPath}.");
            RuleFor(i => i.ValueName).Equal(string.Empty).WithMessage("A google-mdm item names no value.");
            RuleFor(i => i.ValueType).Null().WithMessage("A google-mdm item has no valueType: it writes no value.");
            RuleFor(i => i.Secret).Null().WithMessage("A google-mdm item has no secret.");
            RuleFor(i => i.Data).Must(BaselineItemRules.IsValidStaleDays)
                .WithMessage($"A google-mdm item's data is the days without a successful sync before the enrolment counts as dead: a whole number from {BaselineItemRules.GoogleMdmMinStaleDays} to {BaselineItemRules.GoogleMdmMaxStaleDays}.");
            RuleFor(i => i.Mode).Must(m => m is BaselineItemRules.ModeAudit or BaselineItemRules.ModeEnforce)
                .WithMessage("A google-mdm item's mode is 'audit' or 'enforce'.");
        });

        When(i => i.Kind == BaselineItemRules.KindFact, () =>
        {
            RuleFor(i => i.ValueType).Null().WithMessage("A fact is only read, so it has no valueType.");
            RuleFor(i => i.Data).Null().WithMessage("A fact is only read, so it has no data.");
            RuleFor(i => i.Secret).Null().WithMessage("A fact is only read, so it has no secret.");
            RuleFor(i => i.Mode).Null().WithMessage("A fact is only read, so it has no mode.");
        });
    }
}

/// <summary>The rules across items: keys are unique, and no two settings name the same registry value
/// (the deterministic conflict test; settings that fight through <em>different</em> paths, like Master
/// Daily's sections 3 and 12, are for review to catch).</summary>
public class BaselineItemsValidator : AbstractValidator<IReadOnlyList<BaselineItem>>
{
    public BaselineItemsValidator()
    {
        RuleFor(items => items).NotEmpty().WithMessage("A baseline needs at least one item.");
        RuleFor(items => items.Count).LessThanOrEqualTo(BaselineItemRules.MaxItems).WithName("Items");
        RuleForEach(items => items).NotNull().SetValidator(new BaselineItemValidator()).OverridePropertyName("Items");

        RuleFor(items => items)
            .Must(items => DuplicatesOf(items.Where(i => i is not null).Select(i => i.Key), StringComparer.Ordinal).Count == 0)
            .WithMessage(items => "Item keys must be unique: " + string.Join(", ", DuplicatesOf(items.Where(i => i is not null).Select(i => i.Key), StringComparer.Ordinal)));

        // Two would fight over the same four default-signature values on every check.
        RuleFor(items => items)
            .Must(items => items.Count(i => i is not null && i.IsPerPerson) <= 1)
            .WithMessage("A baseline holds at most one outlook-signature item.");

        // There is one Google enrolment to judge per PC, and two items could disagree about acting on it.
        RuleFor(items => items)
            .Must(items => items.Count(i => i is not null && i.IsGoogleMdm) <= 1)
            .WithMessage("A baseline holds at most one google-mdm item.");

        RuleFor(items => items)
            .Must(items => DuplicatesOf(SettingTargets(items), StringComparer.OrdinalIgnoreCase).Count == 0)
            .WithMessage(items => "Two settings name the same registry value: " + string.Join(", ", DuplicatesOf(SettingTargets(items), StringComparer.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> SettingTargets(IEnumerable<BaselineItem> items) =>
        items.Where(i => i is not null && i.IsSetting).Select(i => $@"{i.Hive}\{i.Path}\{i.ValueName}");

    private static IReadOnlyList<string> DuplicatesOf(IEnumerable<string?> values, StringComparer comparer) =>
        values.Where(v => v is not null).GroupBy(v => v!, comparer).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
}
