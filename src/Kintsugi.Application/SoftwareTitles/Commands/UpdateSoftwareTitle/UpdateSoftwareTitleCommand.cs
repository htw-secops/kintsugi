using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles.Commands.UpdateSoftwareTitle;

/// <summary>Changes a title's mode, detection prefix and secrets, and re-signs it. Name, package id and
/// source cannot change: delete the title and add it again.</summary>
public record UpdateSoftwareTitleCommand(Guid Id, SoftwareMode Mode, string? DetectAs, IReadOnlyList<string> SecretNames)
    : IRequest<SoftwareScreenDto>;
