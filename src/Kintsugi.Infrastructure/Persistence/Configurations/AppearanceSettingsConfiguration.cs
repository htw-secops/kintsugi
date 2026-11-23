using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AppearanceSettingsConfiguration : IEntityTypeConfiguration<AppearanceSettings>
{
    public void Configure(EntityTypeBuilder<AppearanceSettings> builder)
    {
        builder.ToTable("appearance_settings");

        builder.HasKey(s => s.Id);

        // "system" is the longest mode and "spin" the longest animation; 16 leaves room for a mode a
        // later version adds without a migration. The values are checked in the domain, not here.
        builder.Property(s => s.ColourMode).HasMaxLength(16).IsRequired();
        builder.Property(s => s.LogoAnimation).HasMaxLength(16).IsRequired();
        builder.Property(s => s.PresetKey).HasMaxLength(AppearanceSettings.MaxPresetKeyLength).IsRequired();
        // "#RRGGBB".
        builder.Property(s => s.AccentHex).HasMaxLength(7);
        // bytea. The domain caps it at MaxWallpaperBytes, and there is only ever one row.
        builder.Property(s => s.Wallpaper);
    }
}
