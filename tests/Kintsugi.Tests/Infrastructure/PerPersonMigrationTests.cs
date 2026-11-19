using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Kintsugi.Infrastructure.Persistence.Migrations;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Juan's review of PR #80, item 3: rolling back <c>AddPerPersonBaselineItems</c> must work once people have
/// reported. A host then holds one row per person for the same (host, version, item), and the three-column
/// unique index <c>Down()</c> recreates would refuse them, so those rows have to go first.
/// </summary>
/// <remarks>
/// The test project has no Postgres (it uses the in-memory provider), so this reads the operations
/// <c>Down()</c> produces rather than running them. The rollback was also run against a real postgres:16
/// holding two people's rows and the host's own row: it completed, keeping only the host's row.
/// </remarks>
public class PerPersonMigrationTests
{
    [Fact]
    public void Down_DeletesThePerPersonRows_BeforeTheOldUniqueIndexIsRecreated()
    {
        var operations = new AddPerPersonBaselineItems().DownOperations.ToList();

        var delete = operations.FindIndex(o => o is SqlOperation sql
            && sql.Sql.Contains("DELETE FROM patching.host_baseline_item_states", StringComparison.Ordinal)
            && sql.Sql.Contains(@"""Subject"" <> ''", StringComparison.Ordinal));
        var dropSubject = operations.FindIndex(o => o is DropColumnOperation { Name: "Subject", Table: "host_baseline_item_states" });
        var oldIndex = operations.FindIndex(o => o is CreateIndexOperation { Name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey", IsUnique: true });

        Assert.True(delete >= 0, "Down() no longer deletes the per-person rows");
        Assert.True(delete < dropSubject, "the rows must be deleted while Subject still exists to select them");
        Assert.True(delete < oldIndex, "the rows must be gone before the three-column unique index is recreated");
    }
}
