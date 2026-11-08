using System.Collections.Concurrent;

namespace Pitwall.Data;

/// <summary>
/// When each car started each lap: the feed time of the timing update that counted its last
/// lap complete. Telemetry is timed by the car, and arrives in batches a second or two after
/// the timing update for the same moment, so laps can't be split by the order things arrive;
/// they're split by these times instead. The update itself trails the crossing slightly, but
/// by about the same each lap, so comparisons between laps aren't affected.
/// </summary>
public sealed class LapCrossingProcessor(IDateTimeProvider dateTimeProvider)
    : IProcessor<TimingDataPoint>
{
    private readonly ConcurrentDictionary<string, List<(int Lap, DateTimeOffset Utc)>> _starts =
        new();

    public TimingDataPoint Latest { get; private set; } = new();

    public void Process(TimingDataPoint data)
    {
        Latest = data;
        var now = dateTimeProvider.Utc;
        foreach (var (driverNumber, line) in data.Lines)
        {
            if (line.NumberOfLaps is not { } completed)
                continue;
            var starts = _starts.GetOrAdd(driverNumber, _ => []);
            lock (starts)
            {
                // The lap being driven is one more than the laps completed
                if (starts.Count == 0 || starts[^1].Lap < completed + 1)
                    starts.Add((completed + 1, now));
            }
        }
    }

    /// <summary>The lap a car was on at a moment, and when it started, if known.</summary>
    public (int Lap, DateTimeOffset Start)? LapAt(string driverNumber, DateTimeOffset utc)
    {
        if (!_starts.TryGetValue(driverNumber, out var starts))
            return null;
        lock (starts)
        {
            var index = starts.FindLastIndex(x => x.Utc <= utc);
            return index < 0 ? null : (starts[index].Lap, starts[index].Utc);
        }
    }
}
