using System.Globalization;

namespace Pitwall.Data;

/// <summary>The time against the car's best lap at a point of the lap in progress.</summary>
/// <param name="Distance">Where on the lap, in metres along the best lap.</param>
/// <param name="Delta">Seconds behind the best lap there (negative: ahead).</param>
public readonly record struct DeltaPoint(double Distance, double Delta);

/// <summary>A car's lap in progress against its best lap.</summary>
/// <param name="Points">The delta along the lap so far, ending with the live value.</param>
/// <param name="BestLength">The best lap's length in metres, for the scale of a graph.</param>
/// <param name="SectorEnds">Where the first two sectors end along the best lap, in metres.</param>
/// <param name="BestSeconds">The best lap's time.</param>
/// <param name="Live">
/// The delta now. Not smoothed like <see cref="Points"/>, as averaging over the track behind
/// would make it lag.
/// </param>
public sealed record LapDeltaTrace(
    IReadOnlyList<DeltaPoint> Points,
    double BestLength,
    IReadOnlyList<double> SectorEnds,
    double BestSeconds,
    double Live
);

/// <summary>
/// The live delta of each car's lap in progress to its own best lap, as the teams and TV show
/// in qualifying. The time on the current lap at each point is compared with the time the
/// best lap took to reach the same point.
/// <para>
/// Both laps are placed by distance integrated from speed, which drifts by a fraction of a
/// percent. So wherever the current lap has finished a sector, distances are mapped through the
/// sector lines: the point where the current lap's official sector time ran out is where the
/// best lap's did. At those lines the delta is exactly the official sector times' difference,
/// and in between it's interpolated from the telemetry.
/// </para>
/// </summary>
public sealed class LapDelta(TimingDataProcessor timingData, CarTelemetryHistoryProcessor telemetry)
{
    /// <summary>
    /// Deltas bigger than this aren't a push lap (a cool-down lap, or traffic), so aren't shown.
    /// </summary>
    public const double MaxDelta = 3.0;

    // A lap that starts slower than this started from the pit lane: an out lap
    private const int PitExitSpeed = 120;

    /// <summary>
    /// The car's lap in progress against its best, or <c>null</c> when it isn't on a push lap
    /// (in the pits, on an out lap, or too far off its best) or there's no best lap to compare.
    /// </summary>
    public LapDeltaTrace? Trace(string driverNumber)
    {
        var line = timingData.Latest.Lines.GetValueOrDefault(driverNumber);
        if (line is null || line.InPit == true || line.Retired == true || line.Stopped == true)
            return null;
        var best = telemetry.GetBestLap(driverNumber);
        var laps = telemetry.GetLaps(driverNumber);
        if (best is null || best.Samples.Count < 10 || laps.Count == 0)
            return null;
        var current = laps[^1];
        if (current.Lap == best.Lap || current.Samples.Count == 0)
            return null;
        if (current.Samples[0].Speed < PitExitSpeed)
            return null;

        var samples = current.Samples.ToList();
        if (
            telemetry.TryGetLive(driverNumber, out var live)
            && live.Elapsed > samples[^1].Elapsed
            && live.Distance >= samples[^1].Distance
        )
        {
            samples.Add(live);
        }

        // The official sector times of both laps, as cumulative times from the line
        var bestSectors = SectorTimes(
            timingData.DriversByLap.GetValueOrDefault(best.Lap)?.GetValueOrDefault(driverNumber)
        );
        var currentSectors = SectorTimes(line);
        var bestSeconds = best.Samples[^1].Elapsed.TotalSeconds;
        if (line.BestLapTime.ToTimeSpan() is { } official)
            bestSeconds = official.TotalSeconds;

        // Sector lines along the best lap, and along the current lap where it's reached them
        var bestEnds = bestSectors.Select(t => DistanceAtTime(best.Samples, t)).ToList();
        var anchors = new List<(double Current, double Best)> { (0, 0) };
        for (var i = 0; i < Math.Min(currentSectors.Count, bestEnds.Count); i++)
            anchors.Add((DistanceAtTime(samples, currentSectors[i]), bestEnds[i]));

        var points = new List<DeltaPoint>(samples.Count);
        foreach (var sample in samples)
        {
            var distance = MapDistance(anchors, sample.Distance);
            var bestTime = TimeAtDistance(best.Samples, distance);
            if (bestTime is null)
                continue;
            points.Add(new DeltaPoint(distance, sample.Elapsed.TotalSeconds - bestTime.Value));
        }

        if (points.Count == 0)
            return null;
        points = Despike(points);
        var now = points[^1].Delta;
        return Math.Abs(now) > MaxDelta
            ? null
            : new LapDeltaTrace(
                Smooth(points, SmoothingMetres),
                best.Distance,
                bestEnds,
                bestSeconds,
                now
            );
    }

    // Both laps are placed by distance integrated from speed, and a few metres' difference
    // between them at a slow corner is tenths of a second, which shows as a dip that recovers on
    // the exit; averaging over this much track evens those out
    private const double SmoothingMetres = 150;

    /// <summary>
    /// Each point averaged with the points within <paramref name="metres"/> of it, behind it and,
    /// where the lap has got that far, ahead; so the live value at the end averages only what's
    /// behind it.
    /// </summary>
    internal static List<DeltaPoint> Smooth(List<DeltaPoint> points, double metres)
    {
        var result = new List<DeltaPoint>(points.Count);
        var (from, to, sum) = (0, 0, 0.0);
        for (var i = 0; i < points.Count; i++)
        {
            while (to < points.Count && points[to].Distance <= points[i].Distance + (metres / 2))
                sum += points[to++].Delta;
            while (points[from].Distance < points[i].Distance - (metres / 2))
                sum -= points[from++].Delta;
            result.Add(points[i] with { Delta = sum / (to - from) });
        }
        return result;
    }

    /// <summary>
    /// Removes spikes a sample or two wide, which come from the telemetry being sparse (about
    /// four samples a second) where speed changes fast, under braking: each point takes the
    /// median of it and its two neighbours either side.
    /// </summary>
    internal static List<DeltaPoint> Despike(List<DeltaPoint> points)
    {
        if (points.Count < 5)
            return points;
        var result = new List<DeltaPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var window = points[Math.Max(0, i - 2)..Math.Min(points.Count, i + 3)]
                .Select(x => x.Delta)
                .Order()
                .ToList();
            result.Add(points[i] with { Delta = window[window.Count / 2] });
        }
        return result;
    }

    /// <summary>The live delta to the car's best lap, if it's on a push lap.</summary>
    public double? Live(string driverNumber) => Trace(driverNumber)?.Live;

    /// <summary>
    /// The cumulative times at the end of the first two sectors, as far as the lap has them.
    /// </summary>
    internal static List<double> SectorTimes(TimingDataPoint.Driver? line)
    {
        var times = new List<double>();
        var total = 0.0;
        for (var i = 0; i < 2; i++)
        {
            if (
                line?.Sectors.GetValueOrDefault($"{i}")?.Value is not { Length: > 0 } value
                || !double.TryParse(value, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0
            )
                break;
            total += seconds;
            times.Add(total);
        }
        return times;
    }

    /// <summary>
    /// A distance on the current lap mapped to the best lap's distance, through the sector
    /// lines both have reached; past the last one, with the same scale as the stretch before it.
    /// </summary>
    internal static double MapDistance(List<(double Current, double Best)> anchors, double distance)
    {
        for (var i = 1; i < anchors.Count; i++)
        {
            if (distance <= anchors[i].Current || i == anchors.Count - 1)
            {
                var (from, to) = (anchors[i - 1], anchors[i]);
                var span = to.Current - from.Current;
                return span <= 0
                    ? to.Best + (distance - to.Current)
                    : from.Best + ((distance - from.Current) * (to.Best - from.Best) / span);
            }
        }
        return distance;
    }

    /// <summary>When the lap reached a distance, interpolated between samples.</summary>
    internal static double? TimeAtDistance(IReadOnlyList<TelemetrySample> samples, double distance)
    {
        if (samples.Count == 0 || distance > samples[^1].Distance)
            return null;
        if (distance <= samples[0].Distance)
            return samples[0].Elapsed.TotalSeconds;
        var index = BinarySearch(samples, x => x.Distance, distance);
        var (a, b) = (samples[index - 1], samples[index]);
        var f = b.Distance > a.Distance ? (distance - a.Distance) / (b.Distance - a.Distance) : 0;
        return a.Elapsed.TotalSeconds + ((b.Elapsed - a.Elapsed).TotalSeconds * f);
    }

    /// <summary>How far the lap had gone at a time, interpolated between samples.</summary>
    internal static double DistanceAtTime(IReadOnlyList<TelemetrySample> samples, double seconds)
    {
        if (samples.Count == 0)
            return 0;
        if (seconds >= samples[^1].Elapsed.TotalSeconds)
            return samples[^1].Distance;
        if (seconds <= samples[0].Elapsed.TotalSeconds)
            return samples[0].Distance;
        var index = BinarySearch(samples, x => x.Elapsed.TotalSeconds, seconds);
        var (a, b) = (samples[index - 1], samples[index]);
        var span = (b.Elapsed - a.Elapsed).TotalSeconds;
        var f = span > 0 ? (seconds - a.Elapsed.TotalSeconds) / span : 0;
        return a.Distance + ((b.Distance - a.Distance) * f);
    }

    /// <summary>The first index whose value reaches the target (values never decrease).</summary>
    private static int BinarySearch(
        IReadOnlyList<TelemetrySample> samples,
        Func<TelemetrySample, double> value,
        double target
    )
    {
        var (low, high) = (1, samples.Count - 1);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (value(samples[middle]) < target)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
