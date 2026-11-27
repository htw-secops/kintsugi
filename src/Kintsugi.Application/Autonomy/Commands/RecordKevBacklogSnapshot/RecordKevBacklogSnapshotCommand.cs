using MediatR;

namespace Kintsugi.Application.Autonomy.Commands.RecordKevBacklogSnapshot;

/// <summary>Records today's KEV backlog, once a day. Returns false when today already has it or there is no fleet.</summary>
public record RecordKevBacklogSnapshotCommand : IRequest<bool>;
