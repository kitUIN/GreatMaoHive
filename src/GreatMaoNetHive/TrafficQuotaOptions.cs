namespace GreatMaoNetHive;

public sealed class TrafficQuotaOptions
{
    public long? LimitBytes { get; set; }
    public string Period { get; set; } = "monthly";
    public int ResetDay { get; set; } = 1;
    public int UtcOffsetHours { get; set; }

    public void Validate()
    {
        if (LimitBytes < 0) throw new InvalidOperationException("Quota:LimitBytes must be non-negative or null (unlimited).");
        if (Period is not ("daily" or "weekly" or "monthly"))
            throw new InvalidOperationException("Quota:Period must be daily, weekly or monthly.");
        int maximumDay = Period switch { "monthly" => 31, "weekly" => 7, _ => 1 };
        if (ResetDay < 1 || ResetDay > maximumDay)
            throw new InvalidOperationException($"Quota:ResetDay must be 1..{maximumDay} for {Period}.");
        if (UtcOffsetHours is < -12 or > 14)
            throw new InvalidOperationException("Quota:UtcOffsetHours must be -12..14 (fixed whole-hour UTC offset).");
    }

    public (DateTimeOffset Start, DateTimeOffset End) GetPeriod(DateTimeOffset now)
    {
        Validate();
        var offset = TimeSpan.FromHours(UtcOffsetHours);
        var local = now.ToOffset(offset);
        var midnight = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, offset);
        DateTimeOffset start;
        DateTimeOffset end;
        switch (Period)
        {
            case "daily":
                start = midnight;
                end = start.AddDays(1);
                break;
            case "weekly":
                int weekday = ((int)local.DayOfWeek + 6) % 7 + 1; // Monday=1, Sunday=7
                start = midnight.AddDays(-((weekday - ResetDay + 7) % 7));
                end = start.AddDays(7);
                break;
            default:
                var month = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, offset);
                start = MonthlyBoundary(month);
                if (local < start) { month = month.AddMonths(-1); start = MonthlyBoundary(month); }
                // Recalculate from the configured day; February must not shift March's reset day.
                end = MonthlyBoundary(month.AddMonths(1));
                break;
        }
        return (start.ToUniversalTime(), end.ToUniversalTime());
    }

    private DateTimeOffset MonthlyBoundary(DateTimeOffset month) =>
        month.AddDays(Math.Min(ResetDay, DateTime.DaysInMonth(month.Year, month.Month)) - 1);
}
