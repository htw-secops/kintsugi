using MediatR;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles.Commands.CreateSoftwareTitle;

/// <summary>
/// Adds a software title. <paramref name="PackageManager"/> null makes it a custom title, for which
/// <paramref name="Platform"/> names the OS; otherwise the manager's catalog entry decides the OS and
/// <paramref name="PackageId"/> is required.
/// </summary>
/// <param name="CreatedBy">The signed-in administrator, from the session; never from the body.</param>
public record CreateSoftwareTitleCommand(
    string Name,
    string? PackageManager,
    string? Platform,
    string? PackageId,
    string? DetectAs,
    SoftwareMode Mode,
    IReadOnlyList<string> SecretNames,
    string? CreatedBy = null) : IRequest<SoftwareScreenDto>;
