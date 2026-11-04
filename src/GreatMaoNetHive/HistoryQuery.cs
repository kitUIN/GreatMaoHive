namespace GreatMaoNetHive;

public sealed record HistoryQuery(int StepSeconds, long FromSeconds, long ToSeconds)
{
    public string Step => StepSeconds switch { 5 => "5s", 60 => "1m", _ => "1h" };
    public static long Floor(long seconds, int step) => seconds - ((seconds % step + step) % step);
    public static DateTimeOffset Cutoff(int step, DateTimeOffset now) => step switch
    { 5 => now.AddHours(-24), 60 => now.AddDays(-90), _ => now.AddYears(-2) };

    public static HistoryQuery Create(DateTimeOffset from, DateTimeOffset to, string step, DateTimeOffset now)
    {
        if (from >= to || to > now.AddMinutes(5)) throw new ArgumentException("from must precede to; future times beyond five minutes are not allowed.");
        int[] candidates = step switch { "auto" => [5, 60, 3600], "5s" => [5], "1m" => [60], "1h" => [3600], _ => throw new ArgumentException("step must be auto, 5s, 1m or 1h.") };
        foreach (int candidate in candidates)
        {
            long start = Floor(from.ToUnixTimeSeconds(), candidate);
            long end = Floor(to.ToUnixTimeSeconds(), candidate);
            if (to > DateTimeOffset.FromUnixTimeSeconds(end)) end += candidate;
            if (start < Floor(Cutoff(candidate, now).ToUnixTimeSeconds(), candidate) || (end - start) / candidate > 10_000) continue;
            return new(candidate, start, end);
        }
        throw new ArgumentException("Requested resolution has expired or would exceed 10,000 points; shorten the range or use auto.");
    }
}
