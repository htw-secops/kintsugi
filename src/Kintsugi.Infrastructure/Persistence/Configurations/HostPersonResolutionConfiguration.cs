using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostPersonResolutionConfiguration : IEntityTypeConfiguration<HostPersonResolution>
{
    public void Configure(EntityTypeBuilder<HostPersonResolution> builder)
    {
        builder.ToTable("host_person_resolutions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.WindowsUser).HasMaxLength(HostPersonResolution.MaxWindowsUserLength).IsRequired();
        builder.Property(r => r.Email).HasMaxLength(PersonSignature.MaxEmailLength);
        builder.Property(r => r.ResolvedBy).HasMaxLength(20);
        builder.Property(r => r.Reason).HasMaxLength(HostPersonResolution.MaxReasonLength);
        // One row per (host, Windows user). The handler matches the user ignoring case; the agent reports
        // one spelling per session, so an ordinary unique index is what keeps two calls from racing in a pair.
        builder.HasIndex(r => new { r.HostId, r.WindowsUser }).IsUnique();
        // What the 30-day prune reads.
        builder.HasIndex(r => r.LastSeenUtc);
        builder.HasOne<Host>().WithMany().HasForeignKey(r => r.HostId).OnDelete(DeleteBehavior.Cascade);
    }
}
