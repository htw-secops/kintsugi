using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// What one host last reported for one item of one signed <see cref="BaselineVersion"/>.
/// </summary>
/// <remarks>
/// <strong>An agent reports only what changed</strong>, against the states the server handed it in
/// <c>GET /api/baseline</c>, so a compliant fleet sends almost nothing. Every report still touches
/// <see cref="LastCheckedUtc"/> on all of that host's rows for the version, which is what keeps "last
/// checked" true for items nobody needed to resend.
/// </remarks>
public class HostBaselineItemState : BaseEntity
{
    public const int MaxObservedLength = 2048;
    public const int MaxDetailLength = 500;

    /// <summary>The longest subject: an email, at SMTP's 254.</summary>
    public const int MaxSubjectLength = 254;

    public const string MachineSubject = "";

    public Guid HostId { get; private set; }

    public Guid BaselineVersionId { get; private set; }

    public string ItemKey { get; private set; } = default!;

    /// <summary>
    /// Whose state this is: the person's lower-cased email for a per-person item (<c>outlook-signature</c>),
    /// or <see cref="MachineSubject"/> — the empty string — for everything judged once per host. Part of the
    /// row's identity, so one host holds a row per person for the same item.
    /// </summary>
    /// <remarks>
    /// A per-person item can carry a <see cref="MachineSubject"/> row too: it is what an agent too old to know
    /// the kind reports (an error), and what <c>POST /api/baseline-people</c> replaces with "nobody with a
    /// signature signed in" once the host's agent is new enough to ask. A host's count for the item reads
    /// its people's rows first and falls back to that one.
    /// </remarks>
    public string Subject { get; private set; } = MachineSubject;

    public BaselineItemState State { get; private set; }

    /// <summary>The value the agent read, as text (see the agent's <c>baseline::format_value</c>), or
    /// null when the value or key is absent, or the item was not read at all.</summary>
    public string? ObservedValue { get; private set; }

    /// <summary>Why an item is <see cref="BaselineItemState.NotApplicable"/> or in
    /// <see cref="BaselineItemState.Error"/>, in the agent's words.</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset LastCheckedUtc { get; private set; }

    /// <summary>When <see cref="State"/> or <see cref="ObservedValue"/> last changed — drift's own timestamp.</summary>
    public DateTimeOffset LastChangedUtc { get; private set; }

    private HostBaselineItemState()
    {
    }

    public static HostBaselineItemState Create(Guid hostId, Guid baselineVersionId, string itemKey, DateTimeOffset at, string subject = MachineSubject)
    {
        if (hostId == Guid.Empty || baselineVersionId == Guid.Empty || string.IsNullOrWhiteSpace(itemKey))
        {
            throw new DomainException("An item state must name its host, baseline version and item.");
        }

        if (subject is null || subject.Length > MaxSubjectLength)
        {
            throw new DomainException("An item state's subject is an email, or empty for the host itself.");
        }

        return new HostBaselineItemState
        {
            HostId = hostId,
            BaselineVersionId = baselineVersionId,
            ItemKey = itemKey,
            Subject = subject,
            LastCheckedUtc = at,
            LastChangedUtc = at
        };
    }

    public void Record(BaselineItemState state, string? observedValue, string? detail, DateTimeOffset at)
    {
        if (state != State || !string.Equals(observedValue, ObservedValue, StringComparison.Ordinal))
        {
            LastChangedUtc = at;
        }

        State = state;
        ObservedValue = observedValue;
        Detail = detail;
        LastCheckedUtc = at;
        MarkUpdated();
    }

    public void MarkChecked(DateTimeOffset at) => LastCheckedUtc = at;
}
