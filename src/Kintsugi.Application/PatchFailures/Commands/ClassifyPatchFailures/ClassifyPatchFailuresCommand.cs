using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using MediatR;

namespace Kintsugi.Application.PatchFailures.Commands.ClassifyPatchFailures;

/// <summary>
/// Names, with the AI, the outstanding failures the text rules could not (<see cref="FailureClassifier.ScriptError"/>).
/// Run in the background every few minutes, never from the agent's report: see
/// <c>ReportPatchFailureCommandHandler</c>. Returns how many were given a more specific category.
/// </summary>
public record ClassifyPatchFailuresCommand : IRequest<int>
{
    /// <summary>The most failures put to the model in one pass. Each call can take up to
    /// <see cref="AiFailureClassifier.Timeout"/>, and the daily cap bounds the total anyway.</summary>
    public const int MaxPerPass = 10;
}

public class ClassifyPatchFailuresCommandHandler : IRequestHandler<ClassifyPatchFailuresCommand, int>
{
    private readonly IPatchFailureRepository _failures;
    private readonly IHostRepository _hosts;
    private readonly AiFailureClassifier _classifier;
    private readonly IUnitOfWork _unitOfWork;

    public ClassifyPatchFailuresCommandHandler(
        IPatchFailureRepository failures, IHostRepository hosts, AiFailureClassifier classifier, IUnitOfWork unitOfWork)
    {
        _failures = failures;
        _hosts = hosts;
        _classifier = classifier;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(ClassifyPatchFailuresCommand request, CancellationToken cancellationToken)
    {
        var pending = (await _failures.GetAllOutstandingAsync(cancellationToken))
            .Where(f => f.FailureCategory == FailureClassifier.ScriptError && !string.IsNullOrWhiteSpace(f.Details))
            .Take(ClassifyPatchFailuresCommand.MaxPerPass)
            .ToList();

        var named = 0;
        foreach (var failure in pending)
        {
            var host = await _hosts.GetByIdAsync(failure.HostId, cancellationToken);
            var category = await _classifier.ClassifyAsync(
                failure.ApplicationName, failure.Details, host?.Hostname, host?.SerialNumber, DateTimeOffset.UtcNow, cancellationToken);
            if (category == FailureClassifier.ScriptError)
            {
                continue;
            }

            failure.SetCategory(category);
            // The signature includes the category, so it is worked out again: this row then groups with the
            // reports of the same problem that the rules, or the cache, name straight away.
            failure.SetSignature(FailureText.Signature(failure.ApplicationName, category, failure.Details));
            named++;
        }

        if (named > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return named;
    }
}
