using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class ScriptSecretConfiguration : IEntityTypeConfiguration<ScriptSecret>
{
    public void Configure(EntityTypeBuilder<ScriptSecret> builder)
    {
        builder.ToTable("script_secrets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(ScriptSecret.MaxNameLength).IsRequired();
        builder.HasIndex(s => s.Name).IsUnique();
        // Plaintext, on the house rule that the database is not a secret store; the signature is what
        // makes a direct edit of this column something every agent refuses. See ScriptSecret.
        builder.Property(s => s.Value).HasMaxLength(ScriptSecret.MaxValueLength).IsRequired();
        builder.Property(s => s.ValueSignature).HasMaxLength(256).IsRequired();
        builder.Property(s => s.UpdatedBy).HasMaxLength(255);
        builder.Property(s => s.UpdatedUtc).IsRequired();
    }
}
