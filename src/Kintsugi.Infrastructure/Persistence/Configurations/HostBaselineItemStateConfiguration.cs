using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostBaselineItemStateConfiguration : IEntityTypeConfiguration<HostBaselineItemState>
{
    public void Configure(EntityTypeBuilder<HostBaselineItemState> builder)
    {
        builder.ToTable("host_baseline_item_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ItemKey).HasMaxLength(100).IsRequired();
        builder.Property(s => s.ObservedValue).HasMaxLength(HostBaselineItemState.MaxObservedLength);
        builder.Property(s => s.Detail).HasMaxLength(HostBaselineItemState.MaxDetailLength);
        // Empty for the host's own rows, the person's email for a per-person item. Not null, with "" as the
        // default, so existing rows backfill as the host's own and the unique index below has no NULLs to
        // treat as distinct.
        builder.Property(s => s.Subject).HasMaxLength(HostBaselineItemState.MaxSubjectLength).IsRequired().HasDefaultValue(string.Empty);
        // One row per (host, version, item, subject): what ReportBaselineResultsCommandHandler upserts.
        builder.HasIndex(s => new { s.HostId, s.BaselineVersionId, s.ItemKey, s.Subject }).IsUnique();
        // What every compliance count reads: all hosts' rows for one version.
        builder.HasIndex(s => s.BaselineVersionId);
        builder.HasOne<Host>().WithMany().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<BaselineVersion>().WithMany().HasForeignKey(s => s.BaselineVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}
