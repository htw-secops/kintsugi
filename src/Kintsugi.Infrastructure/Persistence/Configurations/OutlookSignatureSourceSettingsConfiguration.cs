using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class OutlookSignatureSourceSettingsConfiguration : IEntityTypeConfiguration<OutlookSignatureSourceSettings>
{
    public void Configure(EntityTypeBuilder<OutlookSignatureSourceSettings> builder)
    {
        builder.ToTable("outlook_signature_source_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.EndpointUrl).HasMaxLength(OutlookSignatureSourceSettings.MaxUrlLength);
        builder.Property(s => s.WindowsUsernameSuffix)
            .HasMaxLength(OutlookSignatureSourceSettings.MaxWindowsUsernameSuffixLength)
            .IsRequired()
            .HasDefaultValue(string.Empty);
        builder.Property(s => s.LastPullStatus).HasMaxLength(20);
        builder.Property(s => s.LastError).HasMaxLength(OutlookSignatureSourceSettings.MaxErrorLength);
        builder.Property(s => s.LastPullSkippedReason).HasMaxLength(OutlookSignatureSourceSettings.MaxErrorLength);
        builder.Ignore(s => s.IsConfigured);
        builder.Ignore(s => s.TokenAudience);
    }
}
