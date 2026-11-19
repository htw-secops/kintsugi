using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class SoftwareTitleConfiguration : IEntityTypeConfiguration<SoftwareTitle>
{
    public void Configure(EntityTypeBuilder<SoftwareTitle> builder)
    {
        builder.ToTable("software_titles");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(SoftwareTitle.MaxNameLength).IsRequired();
        // Same width as upgrade_paths.Platform, because a custom title's script is the upgrade path
        // stored under this exact (Name, Platform).
        builder.Property(t => t.Platform).HasMaxLength(32).IsRequired();
        builder.Property(t => t.PackageManager).HasMaxLength(64);
        builder.Property(t => t.OperatingSystem).HasMaxLength(32).IsRequired();
        builder.Property(t => t.PackageId).HasMaxLength(SoftwareTitle.MaxPackageIdLength);
        builder.Property(t => t.DetectAs).HasMaxLength(SoftwareTitle.MaxDetectAsLength);
        builder.Property(t => t.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        // text[] and uuid[] (Npgsql's mappings for primitive collections), like Baseline's
        // AssignedHostIds: nothing queries from the host side, and a removed host's id just stops matching.
        builder.Property(t => t.SecretNames);
        builder.Property(t => t.AssignedHostIds);
        builder.Property(t => t.AssignedBy).HasMaxLength(255);
        builder.Property(t => t.CreatedBy).HasMaxLength(255);
        builder.Property(t => t.ActionSignature).HasMaxLength(256);
        builder.HasIndex(t => new { t.Name, t.Platform }).IsUnique();
    }
}
