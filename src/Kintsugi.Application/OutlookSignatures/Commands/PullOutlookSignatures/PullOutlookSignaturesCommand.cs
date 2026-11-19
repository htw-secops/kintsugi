using MediatR;

namespace Kintsugi.Application.OutlookSignatures.Commands.PullOutlookSignatures;

/// <summary>
/// One pull from the signature source: replace this server's copies with what it serves now. Run hourly
/// and once at start-up by <c>OutlookSignaturePullBackgroundService</c>.
/// </summary>
/// <remarks>
/// Returns null when there was nothing to do — no settings, or the pull switched off — and otherwise the
/// outcome it also wrote to <c>OutlookSignatureSourceSettings</c>.
/// </remarks>
public record PullOutlookSignaturesCommand : IRequest<OutlookSignaturePullResult?>;

/// <param name="Status"><c>ok</c>, <c>disabled</c> or <c>error</c>, as stored in <c>LastPullStatus</c>.</param>
/// <param name="Count">Copies held after the pull.</param>
public record OutlookSignaturePullResult(string Status, int Count, string? Error);
