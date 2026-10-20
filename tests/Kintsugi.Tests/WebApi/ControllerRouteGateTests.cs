using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Kintsugi.WebApi.Controllers;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// Fails when a route is added with no gate at all. Nothing else does: nginx's client-certificate
/// regex is an exact match that never covers a browser-driven route, and <c>Program.cs</c> exempts
/// all of <c>/api</c> from the sign-in gate, so a route that carries neither
/// <see cref="RequireAdminSessionAttribute"/> nor <see cref="RequireAgentIdentityAttribute"/> is
/// anonymous. The exceptions below are the deliberate ones; adding to the list is the review
/// conversation this test exists to force.
/// </summary>
public class ControllerRouteGateTests
{
    private static readonly HashSet<string> DeliberatelyAnonymous = new()
    {
        // An unenrolled agent has no certificate yet, so enrolment cannot require one.
        "HostsController.Enroll",
        // Public package metadata and the archive itself (CI publishes the same bytes as a GitHub
        // release); Download decides for itself whether to write the enrolment token in.
        "AgentPackagesController.GetAll",
        "AgentPackagesController.GetLatest",
        "AgentPackagesController.Download",
        // Behind nginx's agent-certificate regex; see PatchingPolicyController's remarks.
        "PatchingPolicyController.Get",
        // The routes that decide whether and how to sign in, so they cannot require being signed in.
        "SessionController.Get",
        "SessionController.Challenge",
        "SessionController.Logout",
    };

    [Fact]
    public void EveryRoute_IsGated_OrDeliberatelyAnonymous()
    {
        var ungated = new List<string>();

        foreach (var controller in typeof(SessionController).Assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var classGated = IsGated(controller);

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()))
            {
                var name = $"{controller.Name}.{action.Name}";
                if (!classGated && !IsGated(action) && !DeliberatelyAnonymous.Contains(name))
                {
                    ungated.Add(name);
                }
            }
        }

        Assert.True(
            ungated.Count == 0,
            "Routes with no [RequireAdminSession] or [RequireAgentIdentity]: " + string.Join(", ", ungated));
    }

    [Fact]
    public void DeliberateExceptions_AllStillExist()
    {
        var actions = typeof(SessionController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToHashSet();

        Assert.All(DeliberatelyAnonymous, name => Assert.Contains(name, actions));
    }

    private static bool IsGated(MemberInfo member) =>
        member.GetCustomAttributes<RequireAdminSessionAttribute>(inherit: true).Any()
        || member.GetCustomAttributes<RequireAgentIdentityAttribute>(inherit: true).Any();
}
