using System.Globalization;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings;

/// <summary>Works out where a month's spending stands against the limit, and says so in words.</summary>
public static class AiBudgetStatusBuilder
{
    /// <summary>The first instant of the month <paramref name="now"/> falls in, in UTC. A calendar
    /// month, so "resets on the 1st" means what it says for everyone, whatever their time zone.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now) =>
        new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static AiBudgetStatusDto Build(AiBudgetSettings settings, decimal spentUsd, DateTimeOffset now)
    {
        var budget = settings.MonthlyBudgetUsd;
        if (budget is null)
        {
            return new AiBudgetStatusDto(spentUsd, null, 0, false, false, false, null);
        }

        var percent = (int)Math.Floor(spentUsd * 100m / budget.Value);
        var limitReached = spentUsd >= budget.Value;
        var warnReached = percent >= settings.WarnPercent;
        var blocked = limitReached && settings.StopAtLimit;

        var message = blocked
            ? $"AI budget reached: {Money(spentUsd, settings)} of {Money(budget.Value, settings)} has been spent this month, so no new AI "
              + $"research will be started until {MonthStart(now).AddMonths(1).ToString("MMMM d", CultureInfo.InvariantCulture)} or until an "
              + "administrator raises the budget in Settings → AI Usage. Patching and Check for Updates are not affected."
            : null;

        return new AiBudgetStatusDto(spentUsd, budget, percent, warnReached, limitReached, blocked, message);
    }

    private static string Money(decimal usd, AiBudgetSettings settings) =>
        settings.BudgetCurrency == AiBudgetSettings.Aud
            ? "A$" + (usd * settings.UsdToAudRate).ToString("0.00", CultureInfo.InvariantCulture)
            : "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
