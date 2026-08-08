using System.Diagnostics;
using System.Globalization;

namespace EncounterScope.Core;

public interface IEventClock
{
    DateTimeOffset UtcNow { get; }
    long Timestamp { get; }
    long Frequency { get; }
}
public sealed class SystemEventClock : IEventClock
{
    public static SystemEventClock Instance { get; } = new();

    private SystemEventClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

public static class TimestampFormatting
{
    public static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static double ElapsedSeconds(long origin, long now, long frequency)
    {
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        return Math.Round((now - origin) / (double)frequency, 3, MidpointRounding.AwayFromZero);
    }
}
