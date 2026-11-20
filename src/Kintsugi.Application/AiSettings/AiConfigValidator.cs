using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

public enum AiConfigIssueSeverity
{
    /// <summary>The call cannot work as configured.</summary>
    Error,

    /// <summary>It can work, but probably not as intended.</summary>
    Warning,
}

public record AiConfigIssue(AiConfigIssueSeverity Severity, string Message);

/// <summary>One route as the validator sees it: which feature, on which connection, with which model.</summary>
public record AiConfigRoute(AiFeature Feature, AiConnectionSettings Connection, string Model);

/// <summary>
/// A cheap, offline check that the AI configuration agrees with itself: a Vertex connection names a
/// project and a location, every route has a model id, and the routes do not quietly point at
/// different Google Cloud projects. A pure function of the stored configuration — no network, no
/// secrets — so the Test button can run it before it spends a request, and a bad model or region
/// produces one clear message instead of a failure per application later.
/// </summary>
/// <remarks>
/// It judges what is configured, never what a provider's error text happens to print: Google's
/// policy errors show the project <em>number</em> where the connection holds the project <em>id</em>,
/// and those are the same project.
/// </remarks>
public static class AiConfigValidator
{
    public static IReadOnlyList<AiConfigIssue> Validate(IEnumerable<AiConfigRoute> routes)
    {
        var issues = new List<AiConfigIssue>();
        var list = routes.ToList();

        foreach (var route in list)
        {
            var name = $"{route.Feature} route (connection '{route.Connection.Name}')";

            if (string.IsNullOrWhiteSpace(route.Model))
            {
                issues.Add(new(AiConfigIssueSeverity.Error, $"The {name} has no model id."));
            }
            else if (route.Model.Any(char.IsWhiteSpace))
            {
                issues.Add(new(AiConfigIssueSeverity.Error, $"The {name} has a model id with spaces in it ('{route.Model}')."));
            }
        }

        foreach (var connection in list.Select(r => r.Connection).DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateConnection(connection));
        }

        // One Vertex project is the normal setup. Several is allowed (separate budgets, say), but it
        // is also exactly what a half-finished migration looks like, so it is said out loud.
        var projects = list
            .Select(r => r.Connection)
            .Where(c => c.AuthMode == AiAuthMode.GoogleCloud && !string.IsNullOrWhiteSpace(c.GoogleCloudProject))
            .Select(c => c.GoogleCloudProject!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projects.Count > 1)
        {
            issues.Add(new(
                AiConfigIssueSeverity.Warning,
                $"The routes use {projects.Count} different Google Cloud projects ({string.Join(", ", projects)}). One project is usually intended; "
                + "each needs the model enabled and the same organisation policy."));
        }

        return issues;
    }

    /// <summary>Just one connection, for when no routes are known.</summary>
    public static IReadOnlyList<AiConfigIssue> ValidateConnection(AiConnectionSettings connection)
    {
        var issues = new List<AiConfigIssue>();
        if (connection.AuthMode == AiAuthMode.GoogleCloud)
        {
            if (string.IsNullOrWhiteSpace(connection.GoogleCloudProject))
            {
                issues.Add(new(AiConfigIssueSeverity.Error, $"Connection '{connection.Name}' uses Google Cloud but has no project id."));
            }

            if (string.IsNullOrWhiteSpace(connection.GoogleCloudLocation))
            {
                issues.Add(new(AiConfigIssueSeverity.Error, $"Connection '{connection.Name}' uses Google Cloud but has no location (for example global or us-east5)."));
            }
        }

        return issues;
    }

    /// <summary>The issues as the one-line text the Test button shows; null when there are none.</summary>
    public static string? Summarize(IEnumerable<AiConfigIssue> issues, AiConfigIssueSeverity severity)
    {
        var messages = issues.Where(i => i.Severity == severity).Select(i => i.Message).ToList();
        return messages.Count == 0 ? null : string.Join(" ", messages);
    }
}
