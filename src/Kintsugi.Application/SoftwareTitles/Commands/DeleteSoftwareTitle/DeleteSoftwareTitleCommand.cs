using MediatR;

namespace Kintsugi.Application.SoftwareTitles.Commands.DeleteSoftwareTitle;

/// <summary>Deletes a title. Kintsugi stops acting on it; nothing already installed or removed is
/// undone, and the upgrade path its script lives in is kept, so the app goes on being patched.</summary>
public record DeleteSoftwareTitleCommand(Guid Id) : IRequest<SoftwareScreenDto>;
