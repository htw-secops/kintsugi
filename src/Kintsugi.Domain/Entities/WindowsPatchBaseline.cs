using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The lowest update revision (UBR) a Windows build must have reached to count as patched, e.g. build
/// <c>26200</c>, revision <c>9974</c>. A host reports its own as <c>Host.OperatingSystemVersionId</c>
/// (<c>"26200.9457"</c>); see <c>WindowsPatchCompliance</c> for the comparison.
/// </summary>
/// <remarks>
/// <see cref="EffectiveFromUtc"/> is when Microsoft released that revision. A host below the minimum is
/// only "due" until <see cref="GraceDays"/> after that date, then it is out of compliance.
/// </remarks>
public class WindowsPatchBaseline : BaseEntity
{
    public const int DefaultGraceDays = 14;
    public const int MaxGraceDays = 365;

    /// <summary>The feature build, e.g. <c>"26200"</c>.</summary>
    public string Build { get; private set; } = string.Empty;
    public int MinUbr { get; private set; }
    public DateTimeOffset EffectiveFromUtc { get; private set; }
    public int GraceDays { get; private set; } = DefaultGraceDays;

    private WindowsPatchBaseline()
    {
    }

    public static WindowsPatchBaseline Create(string build, int minUbr, DateTimeOffset effectiveFromUtc, int graceDays = DefaultGraceDays)
    {
        var trimmed = build?.Trim() ?? string.Empty;
        if (trimmed.Length is < 4 or > 6 || !trimmed.All(char.IsAsciiDigit))
        {
            throw new DomainException("A Windows build is four to six digits, e.g. 26200.");
        }

        if (minUbr < 0)
        {
            throw new DomainException("The minimum update revision cannot be negative.");
        }

        if (graceDays is < 0 or > MaxGraceDays)
        {
            throw new DomainException($"Grace must be between 0 and {MaxGraceDays} days.");
        }

        return new WindowsPatchBaseline { Build = trimmed, MinUbr = minUbr, EffectiveFromUtc = effectiveFromUtc, GraceDays = graceDays };
    }
}
