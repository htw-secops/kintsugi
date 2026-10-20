using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// <c>GET /api/session</c> tells the client whether to show "Access denied". It is anonymous, so
/// these also pin down what it must not do: say yes to someone every other route would refuse.
/// </summary>
public class SessionControllerAuthorizedTests
{
    private static async Task<SessionDto> GetAsync(AuthenticationSettings? settings, ClaimsPrincipal user)
    {
        var repository = new Mock<IAuthenticationSettingsRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var controller = new SessionController(Mock.Of<ISender>(), repository.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };

        var result = await controller.Get(CancellationToken.None);
        return Assert.IsType<SessionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static AuthenticationSettings Settings(params string[] admins) =>
        AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, admins);

    private static ClaimsPrincipal SignedIn(string email) =>
        new(new ClaimsIdentity(new[] { new Claim("email", email) }, "TestAuth"));

    [Fact]
    public async Task ListedAdministrator_IsAuthorized_AndShownByLocalPart()
    {
        var dto = await GetAsync(Settings("juan.roman@example.invalid"), SignedIn("Juan.Roman@example.invalid"));

        Assert.True(dto.SignedIn);
        Assert.True(dto.Authorized);
        Assert.Equal("juan.roman", dto.UserName);
    }

    [Fact]
    public async Task SignedInButNotListed_IsNotAuthorized()
    {
        var dto = await GetAsync(Settings("admin@example.invalid"), SignedIn("other@example.invalid"));

        Assert.True(dto.SignedIn);
        Assert.False(dto.Authorized);
    }

    [Fact]
    public async Task EmptyList_AuthorizesEverySignedInUser()
    {
        var dto = await GetAsync(Settings(), SignedIn("anyone@example.invalid"));

        Assert.True(dto.Authorized);
    }

    [Fact]
    public async Task NotSignedIn_IsNotAuthorized_WhenSignInIsRequired()
    {
        var dto = await GetAsync(Settings("admin@example.invalid"), new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(dto.SignedIn);
        Assert.False(dto.Authorized);
    }

    [Fact]
    public async Task NothingSavedYet_IsAuthorized_LikeTheFilter()
    {
        var dto = await GetAsync(null, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.True(dto.Authorized);
    }
}
