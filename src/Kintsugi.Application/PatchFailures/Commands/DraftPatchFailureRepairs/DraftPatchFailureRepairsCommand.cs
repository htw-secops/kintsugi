using System.Collections.Concurrent;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;
using Kintsugi.Domain.Entities;
using MediatR;

namespace Kintsugi.Application.PatchFailures.Commands.DraftPatchFailureRepairs;

/// <summary>
/// Drafts a repaired script, with the AI, for AI-written scripts that keep failing on hosts — and
/// stores it beside the signed script for a human to sign (<c>UpgradePath.ProposeRepair</c>). Run by
/// <c>PatchFailureRepairBackgroundService</c>. Never signs: signing is the only thing that lets a
/// script run as root or SYSTEM across the fleet, so it stays a person's act. Returns what it did.
/// </summary>
public record DraftPatchFailureRepairsCommand : IRequest<DraftPatchFailureRepairsResult>
{
    /// <summary>The most paths put to the AI in one pass. Each is a full research call with web
    /// access, so a backlog of seventy failures drains over hours rather than in one burst.</summary>
    public const int MaxPerPass = 3;
}

/// <param name="Notes">Why each path tried in this pass got no draft — for the log.</param>
public record DraftPatchFailureRepairsResult(int Drafted, IReadOnlyList<string> Notes);

/// <summary>
/// Which paths were put to the AI today without a draft coming back. A singleton, like
/// <see cref="FailureClassificationCache"/>, and lost on restart for the same reason that is fine:
/// a restart costs one repeat attempt per path, where forgetting every pass would spend a research
/// call every thirty minutes on a path the AI cannot repair.
/// </summary>
public class RepairDraftAttempts
{
    private readonly ConcurrentDictionary<string, DateOnly> _triedOn = new();

    public bool TriedToday(string key, DateTimeOffset nowUtc) =>
        _triedOn.TryGetValue(key, out var day) && day == DateOnly.FromDateTime(nowUtc.UtcDateTime);

    public void MarkTried(string key, DateTimeOffset nowUtc) => _triedOn[key] = DateOnly.FromDateTime(nowUtc.UtcDateTime);
}

public class DraftPatchFailureRepairsCommandHandler : IRequestHandler<DraftPatchFailureRepairsCommand, DraftPatchFailureRepairsResult>
{
    /// <summary>Failures the script did not cause: a repaired script would fail the same way on the
    /// next flaky network, full disk or open application, so drafting one spends a research call on
    /// nothing. The agents retry these on their own.</summary>
    private static readonly HashSet<string> TransientCategories = new(StringComparer.Ordinal)
    {
        FailureClassifier.NetworkOrCertificate, FailureClassifier.FileInUse, FailureClassifier.DiskSpace,
    };

    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _paths;
    private readonly IAiBudgetService _budget;
    private readonly RepairDraftAttempts _attempts;
    private readonly ISender _sender;

    public DraftPatchFailureRepairsCommandHandler(
        IPatchFailureRepository failures, IUpgradePathRepository paths, IAiBudgetService budget, RepairDraftAttempts attempts, ISender sender)
    {
        _failures = failures;
        _paths = paths;
        _budget = budget;
        _attempts = attempts;
        _sender = sender;
    }

    public async Task<DraftPatchFailureRepairsResult> Handle(DraftPatchFailureRepairsCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        var drafted = 0;
        var tried = 0;

        foreach (var group in Eligible(await _failures.GetAllOutstandingAsync(cancellationToken), now))
        {
            if (tried == DraftPatchFailureRepairsCommand.MaxPerPass)
            {
                break;
            }

            // The newest failure's output is the one that describes what the script does now.
            var failure = group.OrderByDescending(f => f.LastFailedUtc).First();
            var path = await _paths.GetAsync(failure.ApplicationName, failure.Platform!, cancellationToken);
            if (path?.Script is null || !NeedsDraft(path, group))
            {
                continue;
            }

            try
            {
                // Checked here as well as by the research client, because the client's refusal is
                // swallowed into a note and would mark the path tried for the day; this way the
                // pass just stops, and the next one after the limit lifts starts where it left off.
                await _budget.EnsureWithinBudgetAsync(cancellationToken);
            }
            catch (AiBudgetReachedException ex)
            {
                notes.Add(ex.Message);
                break;
            }

            tried++;
            var key = Key(group.Key);
            var label = $"{failure.ApplicationName} ({failure.Platform})";

            // The same prompt the Failed Updates screen's fix panel loads: the ordinary research
            // prompt with the repair brief appended. It also answers "no prompt" for a managed-
            // elsewhere application and when the AI is not configured, and that answer is kept.
            var prompt = await _sender.Send(new GetUpgradePathPromptQuery(failure.ApplicationName, failure.Platform, failure.Id), cancellationToken);
            if (!prompt.Available || string.IsNullOrWhiteSpace(prompt.Prompt))
            {
                _attempts.MarkTried(key, now);
                notes.Add($"{label}: {prompt.Reason ?? "no prompt is available"}");
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            var outcome = await _sender.Send(
                new RefreshUpgradePathCommand(failure.ApplicationName, failure.Platform, prompt.Prompt, RepairDraftFor: failure.Id),
                cancellationToken);

            // Read back rather than trusted from the outcome: the draft is what the screen will show,
            // so the row holding it is the proof.
            var after = await _paths.GetAsync(failure.ApplicationName, failure.Platform!, cancellationToken);
            if (after?.CandidateFailureId == failure.Id && after.CandidateDraftedUtc >= started)
            {
                drafted++;
                continue;
            }

            _attempts.MarkTried(key, now);
            notes.Add($"{label}: {outcome.Results?.FirstOrDefault()?.Notes ?? outcome.ErrorMessage ?? "no repair was drafted"}");
        }

        return new DraftPatchFailureRepairsResult(drafted, notes);
    }

    /// <summary>
    /// The outstanding failures worth a draft, grouped by the path whose script failed, the paths
    /// failing on the most hosts first.
    /// </summary>
    /// <remarks>
    /// Only the Windows and macOS buckets: those are the AI-researched scripts. A package manager's
    /// script is one text the server writes for every application it manages, and the AI refuses
    /// Linux. A single failure on a single host is not enough — "failed twice, or on two hosts"
    /// is what separates a broken script from one bad afternoon.
    /// </remarks>
    private IEnumerable<IGrouping<(string Name, string Platform), PatchFailure>> Eligible(IEnumerable<PatchFailure> outstanding, DateTimeOffset now) =>
        outstanding
            .Where(f => f.Platform is PlatformBucket.Windows or PlatformBucket.MacOs)
            .Where(f => !OperatingSystemUpdateFailure.IsOperatingSystem(f.ApplicationName))
            .Where(f => f.FailureCategory is null || !TransientCategories.Contains(f.FailureCategory))
            .GroupBy(f => (Name: f.ApplicationName.ToLowerInvariant(), Platform: f.Platform!))
            .Where(g => g.Any(f => f.FailureCount >= 2) || g.Select(f => f.HostId).Distinct().Count() >= 2)
            // Before the cap, not after: a path the AI could not repair today would otherwise sit at the
            // head of every pass and nothing behind it would ever be tried.
            .Where(g => !_attempts.TriedToday(Key(g.Key), now))
            .OrderByDescending(g => g.Select(f => f.HostId).Distinct().Count())
            .ThenByDescending(g => g.Sum(f => f.FailureCount));

    /// <summary>
    /// No draft yet, or a failure on this path that first happened after the draft was written.
    /// </summary>
    /// <remarks>
    /// <c>FirstFailedUtc</c>, not <c>LastFailedUtc</c>. The signed script keeps running until somebody
    /// signs the draft, so it fails again every patch cycle and moves <c>LastFailedUtc</c> each time;
    /// comparing that would redraft every cycle, spending a research call and replacing the draft
    /// under whoever is reviewing it. A failure that *first* happened after the draft — another host,
    /// another application version — is one the draft never saw. A different script is covered too:
    /// replacing the script drops the draft (<c>UpgradePath.Apply</c>), so the path has none.
    /// </remarks>
    private static bool NeedsDraft(UpgradePath path, IEnumerable<PatchFailure> failures) =>
        path.CandidateDraftedUtc is not { } draftedUtc || failures.Any(f => f.FirstFailedUtc > draftedUtc);

    private static string Key((string Name, string Platform) path) => $"{path.Platform}\n{path.Name}";
}
