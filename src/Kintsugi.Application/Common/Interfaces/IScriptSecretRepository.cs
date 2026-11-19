using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IScriptSecretRepository
{
    Task<IReadOnlyList<ScriptSecret>> GetAllAsync(CancellationToken cancellationToken);

    Task<ScriptSecret?> GetAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(ScriptSecret secret, CancellationToken cancellationToken);

    void Remove(ScriptSecret secret);
}
