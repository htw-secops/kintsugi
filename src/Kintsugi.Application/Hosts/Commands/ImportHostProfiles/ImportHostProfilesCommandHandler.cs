using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;

namespace Kintsugi.Application.Hosts.Commands.ImportHostProfiles;

public class ImportHostProfilesCommandHandler : IRequestHandler<ImportHostProfilesCommand, ImportHostProfilesResult>
{
    private readonly IHostRepository _hosts;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public ImportHostProfilesCommandHandler(IHostRepository hosts, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task<ImportHostProfilesResult> Handle(ImportHostProfilesCommand request, CancellationToken cancellationToken)
    {
        // The last row for a serial wins: an export can list an asset twice (a checkout history), and the newest line is
        // the one at the end.
        var rows = request.Rows
            .GroupBy(r => r.SerialNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var hosts = await _hosts.GetBySerialNumbersAsync(rows.Keys.ToList(), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var matched = 0;
        var updated = 0;

        foreach (var host in hosts)
        {
            if (!rows.TryGetValue(host.SerialNumber.Trim(), out var row))
            {
                continue;
            }

            matched++;

            // An empty department, location or model means "the export had nothing here", not "clear it". The person
            // is different: the browser only sends those columns when the administrator ticked them, and an empty
            // "Checked Out To" means the device was checked back in, so an empty name or email clears what is stored
            // rather than leaving the previous person on it for good. Null (column not sent) still keeps it.
            static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

            var changed = host.SetProfile(
                department: Given(row.Department),
                location: Given(row.Location),
                model: Given(row.Model),
                assignedName: row.AssignedName,
                assignedEmail: row.AssignedEmail);
            if (changed.Count == 0)
            {
                continue;
            }

            updated++;
            await _events.HostProfileChangedAsync(host.Id, request.Actor, "Imported: " + string.Join(", ", changed), now, cancellationToken);
        }

        if (updated > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ImportHostProfilesResult(matched, rows.Count - matched, updated);
    }
}
