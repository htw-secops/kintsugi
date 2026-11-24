namespace Kintsugi.Application.OutlookSignatures;

/// <summary>
/// The signature source's bulk read, as it arrives: <c>{"status":"ok","signatures":[...]}</c> or
/// <c>{"status":"disabled"}</c>. Mirrors the content service's response by hand; nothing cross-checks it,
/// so a field renamed there shows up as a pull refused here, with the reason in
/// <see cref="Kintsugi.Domain.Entities.OutlookSignatureSourceSettings.LastError"/>.
/// </summary>
/// <param name="Status"><see cref="StatusOk"/> or <see cref="StatusDisabled"/>; anything else is malformed.</param>
/// <param name="Signatures">Present for <see cref="StatusOk"/>. Every field nullable so a missing one is a
/// refusal with a reason rather than a deserialisation error with none.</param>
public record OutlookSignatureSourceResponse(string? Status, IReadOnlyList<SourceSignature?>? Signatures)
{
    public const string StatusOk = "ok";
    public const string StatusDisabled = "disabled";
}

/// <param name="RenderedAt">ISO 8601; an offset-less value is read as UTC.</param>
/// <param name="Reply">False when the person turned the signature off for Outlook replies at the source.
/// Absent (a source that predates the switch) means on, so it is not a refusal.</param>
public record SourceSignature(string? Email, string? Html, string? Text, string? RenderedAt, bool? Reply = null);
