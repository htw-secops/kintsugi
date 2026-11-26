using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScript;

/// <summary>One upgrade path's script, read when the Applications screen's "View script" is clicked. The list
/// itself (<see cref="UpgradePathSummaryDto"/>) carries only whether a script exists, because it is polled every
/// minute and the scripts were most of its bytes. Null when the row or its script is gone.</summary>
public record GetUpgradePathScriptQuery(string ApplicationName, string Platform) : IRequest<UpgradePathScriptDto?>;

/// <summary>Mirrored by <c>web/lib/data/repositories/application_repository_impl.dart</c>.</summary>
public record UpgradePathScriptDto(string ApplicationName, string Platform, string Script);
