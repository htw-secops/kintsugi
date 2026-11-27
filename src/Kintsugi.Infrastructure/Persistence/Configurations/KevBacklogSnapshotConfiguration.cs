using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class KevBacklogSnapshotConfiguration : IEntityTypeConfiguration<KevBacklogSnapshot>
{
    public void Configure(EntityTypeBuilder<KevBacklogSnapshot> builder)
    {
        builder.ToTable("kev_backlog_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SnapshotDateUtc).IsRequired();
        builder.HasIndex(s => s.SnapshotDateUtc).IsUnique();
        builder.Property(s => s.TopCveId).HasMaxLength(KevBacklogSnapshot.MaxCveIdLength);
    }
}
