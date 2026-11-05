using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AiBudgetSettingsConfiguration : IEntityTypeConfiguration<AiBudgetSettings>
{
    public void Configure(EntityTypeBuilder<AiBudgetSettings> builder)
    {
        builder.ToTable("ai_budget_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.BudgetAmount).HasPrecision(18, 2);
        builder.Property(s => s.BudgetCurrency).HasMaxLength(3).IsRequired();
        builder.Property(s => s.UsdToAudRate).HasPrecision(18, 6);

        // Derived from the amount, currency and rate: not a column.
        builder.Ignore(s => s.MonthlyBudgetUsd);
    }
}
