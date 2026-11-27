using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class DeviceInvestigationConfiguration : IEntityTypeConfiguration<DeviceInvestigation>
{
    public void Configure(EntityTypeBuilder<DeviceInvestigation> builder)
    {
        builder.ToTable("device_investigations");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Why).HasMaxLength(DeviceInvestigation.MaxWhyLength).IsRequired();
        builder.Property(i => i.LikelyCause).HasMaxLength(DeviceInvestigation.MaxCauseLength);
        builder.Property(i => i.Report).HasMaxLength(DeviceInvestigation.MaxReportLength);
        builder.Property(i => i.AlertError).HasMaxLength(DeviceInvestigation.MaxAlertErrorLength);
        builder.Ignore(i => i.AlertDue);

        // The newest of one device (the report page and the cooldown) and the unfinished ones (each pass).
        builder.HasIndex(i => new { i.HostId, i.OpenedUtc });
        builder.HasIndex(i => i.OpenedUtc);

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(i => i.HostId)
            // A removed host's investigations go with it.
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DeviceInvestigationSettingsConfiguration : IEntityTypeConfiguration<DeviceInvestigationSettings>
{
    public void Configure(EntityTypeBuilder<DeviceInvestigationSettings> builder)
    {
        builder.ToTable("device_investigation_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SlackWebhookUrl).HasMaxLength(2048);
        builder.Property(s => s.ConsoleBaseUrl).HasMaxLength(2048);
    }
}
