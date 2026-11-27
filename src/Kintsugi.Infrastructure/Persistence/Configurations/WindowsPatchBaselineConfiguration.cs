using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class WindowsPatchBaselineConfiguration : IEntityTypeConfiguration<WindowsPatchBaseline>
{
    public void Configure(EntityTypeBuilder<WindowsPatchBaseline> builder)
    {
        builder.ToTable("windows_patch_baselines");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Build).IsRequired().HasMaxLength(6);
        builder.HasIndex(b => b.Build).IsUnique();
        builder.Property(b => b.MinUbr).IsRequired();
        builder.Property(b => b.EffectiveFromUtc).IsRequired();
        builder.Property(b => b.GraceDays).IsRequired();
    }
}
