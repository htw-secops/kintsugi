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
    public const string ScopeMachine = "machine";
    public const string ScopeUser = "user";
    public const string HiveMachine = "HKLM";
    public const string HiveUser = "HKCU";
    public const string ModeAudit = "audit";

    public static readonly IReadOnlyList<string> ValueTypes = new[] { "dword", "qword", "string", "expandString", "multiString", "binary" };

    /// <summary>The most items one version may hold: far above Master Daily's 42, and what stops a
    /// hostile body creating an unbounded document.</summary>
    public const int MaxItems = 500;

    public const int MaxKeyLength = 100;
    public const int MaxPathLength = 512;
    public const int MaxValueNameLength = 255;
    public const int MaxDataLength = 2048;
    public const int MaxReasonLength = 1000;

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

public class BaselineItemValidator : AbstractValidator<BaselineItem>
{
    public BaselineItemValidator()
    {
        RuleFor(i => i.Key).NotEmpty().MaximumLength(BaselineItemRules.MaxKeyLength)
            .Matches(BaselineItemRules.KeyPattern).WithMessage("An item key is lower-case letters, digits, '.', '_' and '-'.");
        RuleFor(i => i.Kind).Must(k => k is BaselineItemRules.KindSetting or BaselineItemRules.KindFact)
            .WithMessage("An item's kind is 'setting' or 'fact'.");
        RuleFor(i => i.Scope).Must(s => s is BaselineItemRules.ScopeMachine or BaselineItemRules.ScopeUser)
            .WithMessage("An item's scope is 'machine' or 'user'.");
        RuleFor(i => i.Hive)
            .Must((item, hive) => hive == (item.Scope == BaselineItemRules.ScopeUser ? BaselineItemRules.HiveUser : BaselineItemRules.HiveMachine))
            .WithMessage("A machine item is under HKLM and a user item under HKCU.");
        RuleFor(i => i.Path).NotEmpty().MaximumLength(BaselineItemRules.MaxPathLength)
            .Must(p => p is not null && !p.StartsWith('\\') && !p.EndsWith('\\') && !p.Contains(@"\\") && !p.Contains('/'))
            .WithMessage("A key path is backslash-separated with no leading, trailing or doubled backslash.");
        RuleFor(i => i.ValueName).NotNull().MaximumLength(BaselineItemRules.MaxValueNameLength);
        RuleFor(i => i.Reason).MaximumLength(BaselineItemRules.MaxReasonLength);

        When(i => i.Kind == BaselineItemRules.KindSetting, () =>
        {
            RuleFor(i => i.ValueType).Must(t => t is not null && BaselineItemRules.ValueTypes.Contains(t))
                .WithMessage("A setting's valueType is one of: " + string.Join(", ", BaselineItemRules.ValueTypes) + ".");
            // No write path exists in this step, so a setting that claims to be enforced would be a
            // promise nothing keeps.
            RuleFor(i => i.Mode).Equal(BaselineItemRules.ModeAudit).WithMessage("Only 'audit' mode exists in this step.");
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

        RuleFor(items => items)
            .Must(items => DuplicatesOf(SettingTargets(items), StringComparer.OrdinalIgnoreCase).Count == 0)
            .WithMessage(items => "Two settings name the same registry value: " + string.Join(", ", DuplicatesOf(SettingTargets(items), StringComparer.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> SettingTargets(IEnumerable<BaselineItem> items) =>
        items.Where(i => i is not null && i.IsSetting).Select(i => $@"{i.Hive}\{i.Path}\{i.ValueName}");

    private static IReadOnlyList<string> DuplicatesOf(IEnumerable<string?> values, StringComparer comparer) =>
        values.Where(v => v is not null).GroupBy(v => v!, comparer).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
}
