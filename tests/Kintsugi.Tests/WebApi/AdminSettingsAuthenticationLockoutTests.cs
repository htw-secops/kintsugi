using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The guard that stops an administrator saving an allowlist they are not on: the very next
/// request would be a 403 and nobody could repair the list from the UI.
/// </summary>
public class AdminSettingsAuthenticationLockoutTests
{
    private readonly Mock<ISender> _sender = new();

    private AdminSettingsController Controller(params Claim[] claims)
    {
        _sender.Setup(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationSettingsDto.NotConfigured());

        return new AdminSettingsController(
            _sender.Object, Mock.Of<IOptionsMonitorCache<OpenIdConnectOptions>>(), Mock.Of<IVantaSyncCoordinator>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
            }
        };
    }

    private static UpdateAuthenticationSettingsCommand Command(bool enabled, params string[] admins) =>
        new(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, enabled, admins);

    [Fact]
    public async Task ListWithoutTheCaller_IsRefused_WhenSignInIsRequired()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"));

        await Assert.ThrowsAsync<DomainException>(() =>
            controller.UpdateAuthentication(Command(true, "someone.else@example.invalid"), CancellationToken.None));
        _sender.Verify(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListWithTheCaller_IsSaved_CaseInsensitively()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"));

        await controller.UpdateAuthentication(Command(true, "ME@Example.invalid"), CancellationToken.None);

        _sender.Verify(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyList_IsSaved()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"));

        await controller.UpdateAuthentication(Command(true), CancellationToken.None);

        _sender.Verify(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListWithoutTheCaller_IsSaved_WhenSignInIsNotRequired()
    {
        var controller = Controller();

        await controller.UpdateAuthentication(Command(false, "someone.else@example.invalid"), CancellationToken.None);

        _sender.Verify(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
