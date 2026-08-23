using System.Globalization;

namespace AIMediaWorker.Subtitle;

public static class SubtitleTime
{
    public static long FromTimeSpan(TimeSpan value) => checked(value.Ticks / 10);
    public static TimeSpan ToTimeSpan(long microseconds) => TimeSpan.FromTicks(checked(microseconds * 10));

    public static long Parse(string value)
    {
        var text = value.Trim().Replace(',', '.');
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) throw new FormatException($"Invalid subtitle timestamp: {value}");
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = decimal.Parse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (minutes is < 0 or > 59 || seconds is < 0 or >= 60 || hours < 0) throw new FormatException($"Invalid subtitle timestamp: {value}");
        return checked((long)((hours * 3600m + minutes * 60m + seconds) * 1_000_000m));
    }

    public static string FormatSrt(long microseconds)
    {
        var ts = ToTimeSpan(Math.Max(0, microseconds));
        return $"{(long)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    public static string FormatVtt(long microseconds) => FormatSrt(microseconds).Replace(',', '.');

    public static string FormatAss(long microseconds)
    {
        var ts = ToTimeSpan(Math.Max(0, microseconds));
        return $"{(long)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }
}
