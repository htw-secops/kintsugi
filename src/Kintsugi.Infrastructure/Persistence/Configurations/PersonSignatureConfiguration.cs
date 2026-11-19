using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PersonSignatureConfiguration : IEntityTypeConfiguration<PersonSignature>
{
    public void Configure(EntityTypeBuilder<PersonSignature> builder)
    {
        builder.ToTable("person_signatures");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Email).HasMaxLength(PersonSignature.MaxEmailLength).IsRequired();
        // One copy per person. The entity lower-cases on the way in, so an ordinary unique index is enough.
        builder.HasIndex(s => s.Email).IsUnique();
        // Unbounded text: the 256 KiB cap is in UTF-8 bytes, which a character-length column cannot state.
        builder.Property(s => s.Html).IsRequired();
        builder.Property(s => s.Text).IsRequired();
        builder.Property(s => s.Sha256).HasMaxLength(64).IsRequired();
    }
}
