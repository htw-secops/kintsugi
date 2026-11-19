using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Commands.TestAiConnection;

/// <summary>Sends one tiny prompt down a stored connection — proves the URL, the credential and the
/// model name before a scan finds out the hard way.</summary>
public record TestAiConnectionCommand(Guid ConnectionId, string Model) : IRequest<AiConnectionTestResultDto>;

public class TestAiConnectionCommandHandler : IRequestHandler<TestAiConnectionCommand, AiConnectionTestResultDto>
{
    private readonly IAiConnectionRepository _connections;
    private readonly IAiConnectionProbe _probe;
    private readonly IAiFeatureRouteRepository _routes;

    public TestAiConnectionCommandHandler(IAiConnectionRepository connections, IAiConnectionProbe probe, IAiFeatureRouteRepository routes)
    {
        _connections = connections;
        _probe = probe;
        _routes = routes;
    }

    public async Task<AiConnectionTestResultDto> Handle(TestAiConnectionCommand request, CancellationToken cancellationToken)
    {
        var connection = await _connections.GetAsync(request.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"AI connection {request.ConnectionId} was not found.");

        var settings = ToSettings(connection);

        // The configuration is checked first, offline: a missing project or model id is reported as
        // such without spending a request, and a mix of projects across routes rides along as a
        // warning on a test that otherwise passes.
        var routes = new List<AiConfigRoute> { new(Domain.Enums.AiFeature.ScriptResearch, settings, request.Model) };
        var connectionsById = (await _connections.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        foreach (var route in await _routes.GetAllAsync(cancellationToken))
        {
            if (connectionsById.TryGetValue(route.ConnectionId, out var routed))
            {
                routes.Add(new AiConfigRoute(route.Feature, ToSettings(routed), route.Model));
            }
        }

        // Only this connection's own problems can stop its Test; another route's belong in a warning.
        var issues = AiConfigValidator.Validate(routes);
        var mine = issues.Where(i => i.Severity == AiConfigIssueSeverity.Error && i.Message.Contains($"'{connection.Name}'")).ToList();
        if (AiConfigValidator.Summarize(mine, AiConfigIssueSeverity.Error) is { } blocking)
        {
            return new AiConnectionTestResultDto(false, null, AiProviderMisconfiguredException.Prefix + blocking, 0);
        }

        var result = await _probe.ProbeAsync(settings, request.Model, cancellationToken);
        if (result.Succeeded && AiConfigValidator.Summarize(issues, AiConfigIssueSeverity.Warning) is { } warning)
        {
            return result with { Reply = $"{result.Reply} (Warning: {warning})" };
        }

        return result;
    }

    private static AiConnectionSettings ToSettings(Domain.Entities.AiConnection c) => new(
        c.Name, c.Protocol, c.BaseUrl, c.AuthMode, c.ApiKey, c.GoogleCloudProject, c.GoogleCloudLocation, c.UseHostedWebSearch);
}
