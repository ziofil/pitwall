namespace Pitwall.Data;

/// <summary>A lap time sample used for pace and degradation calculations.</summary>
public readonly record struct LapSample(int Lap, double Seconds);

/// <summary>Where a pit loss estimate comes from.</summary>
public enum PitLossSource
{
    /// <summary>Measured from the stops made so far in this session.</summary>
    Observed,

    /// <summary>The circuit's typical value, before any stops have been made.</summary>
    Circuit,

    /// <summary>A generic value, when neither stops nor circuit data are available.</summary>
    Default,

    /// <summary>The circuit's typical value under a safety car.</summary>
    SafetyCar,

    /// <summary>The circuit's typical value under a virtual safety car.</summary>
    VirtualSafetyCar,
}

/// <summary>The estimated time lost by making a pit stop, and how it was derived.</summary>
/// <param name="Seconds">Estimated total time lost compared to staying out, in seconds.</param>
/// <param name="Samples">How many observed stops the estimate is based on.</param>
/// <param name="Source">Where the estimate comes from.</param>
public readonly record struct PitLossEstimate(double Seconds, int Samples, PitLossSource Source)
{
    public bool IsDefault => Source == PitLossSource.Default;
}

/// <summary>Where a car would rejoin if it pitted now, assuming nobody else pits.</summary>
/// <param name="Position">The race position the car would rejoin in.</param>
/// <param name="AheadDriverNumber">The car it would rejoin behind, if any.</param>
/// <param name="GapToAhead">Gap to <paramref name="AheadDriverNumber"/> on rejoin, in seconds.</param>
/// <param name="BehindDriverNumber">The car it would rejoin ahead of, if any.</param>
/// <param name="GapToBehind">Gap to <paramref name="BehindDriverNumber"/> on rejoin, in seconds.</param>
public sealed record RejoinPrediction(
    int Position,
    string? AheadDriverNumber,
    double? GapToAhead,
    string? BehindDriverNumber,
    double? GapToBehind
);

/// <summary>
/// Pure calculations used by the strategy pages. They only use data available in the public
/// live timing feed, so they are estimates of what a team's own tools would calculate.
/// </summary>
public static class RaceStrategy
{
    /// <summary>Used until a stop has been observed in the session.</summary>
    public const double DefaultPitLossSeconds = 22.0;

    // Laps slower than this fraction of the driver's typical pace are treated as not
    // representative (traffic, safety car, incidents) and ignored for pace calculations.
    private const double SlowLapThreshold = 1.07;

    /// <summary>
    /// Returns the representative ("clean") lap times for a driver from the per-lap snapshots:
    /// excludes the first lap, pit in/out laps, and laps slower than 107% of the driver's median.
    /// </summary>
    /// <param name="driversByLap">Lap number → driver number → snapshot at the end of that lap.</param>
    /// <param name="driverNumber">The driver to get laps for.</param>
    /// <param name="fromLap">Only laps after this lap number are included.</param>
    public static List<LapSample> GetCleanLaps(
        IReadOnlyDictionary<int, Dictionary<string, TimingDataPoint.Driver>> driversByLap,
        string driverNumber,
        int fromLap = 0
    )
    {
        var laps = driversByLap
            .Where(x => x.Key > 1 && x.Key > fromLap)
            .Select(x => (Lap: x.Key, Line: x.Value.GetValueOrDefault(driverNumber)))
            .Where(x => x.Line is not null && !x.Line.IsPitLap)
            .Select(x => (x.Lap, Time: x.Line!.LastLapTime?.ToTimeSpan()))
            .Where(x => x.Time.HasValue && x.Time.Value > TimeSpan.Zero)
            .Select(x => new LapSample(x.Lap, x.Time!.Value.TotalSeconds))
            .OrderBy(x => x.Lap)
            .ToList();

        if (laps.Count == 0)
            return laps;

        var median = Median(laps.Select(x => x.Seconds));
        return laps.Where(x => x.Seconds <= median * SlowLapThreshold).ToList();
    }

    /// <summary>
    /// Estimates the time lost by a pit stop from stops already made in this session:
    /// for each stop, the in-lap and out-lap times minus what the cars that didn't pit lapped
    /// in on those same laps. Comparing against the field on the same laps keeps the estimate
    /// right when conditions change, e.g. stops for intermediates as rain arrives.
    /// Until a stop has been observed it uses the circuit's typical pit loss, if known, and
    /// otherwise <see cref="DefaultPitLossSeconds"/>.
    /// </summary>
    public static PitLossEstimate EstimatePitLoss(
        IReadOnlyDictionary<int, Dictionary<string, TimingDataPoint.Driver>> driversByLap,
        PitLossTimes? circuit = null
    )
    {
        // The typical lap time of cars not pitting, per lap
        var fieldPace = new Dictionary<int, double>();
        foreach (var (lap, lines) in driversByLap.Where(x => x.Key > 1))
        {
            var times = lines
                .Values.Where(x => !x.IsPitLap)
                .Select(x => x.LastLapTime?.ToTimeSpan()?.TotalSeconds)
                .Where(x => x > 0)
                .Select(x => x!.Value)
                .ToList();
            if (times.Count >= 3)
                fieldPace[lap] = Median(times);
        }

        var losses = new List<double>();
        var driverNumbers = driversByLap.Values.SelectMany(x => x.Keys).Distinct();

        foreach (var driverNumber in driverNumbers)
        {
            // Find runs of consecutive pit laps (normally exactly an in-lap and an out-lap)
            var pitLaps = driversByLap
                .Where(x => x.Key > 1)
                .Where(x => x.Value.GetValueOrDefault(driverNumber)?.IsPitLap == true)
                .Select(x => x.Key)
                .Order()
                .ToList();

            foreach (var run in ConsecutiveRuns(pitLaps).Where(x => x.Count == 2))
            {
                var times = run.Select(lap =>
                        driversByLap[lap][driverNumber].LastLapTime?.ToTimeSpan()?.TotalSeconds
                    )
                    .ToList();
                if (times.Any(x => x is null) || !run.All(fieldPace.ContainsKey))
                    continue;

                var loss = times.Sum(x => x!.Value) - run.Sum(lap => fieldPace[lap]);

                // Discard stops that clearly weren't normal (penalties, repairs, safety cars)
                if (loss is > 8 and < 45)
                {
                    losses.Add(loss);
                }
            }
        }

        return losses.Count > 0
                ? new PitLossEstimate(Median(losses), losses.Count, PitLossSource.Observed)
            : circuit is null ? new PitLossEstimate(DefaultPitLossSeconds, 0, PitLossSource.Default)
            : new PitLossEstimate(circuit.Normal, 0, PitLossSource.Circuit);
    }

    /// <summary>
    /// The pit loss right now. A stop under a safety car or VSC costs much less, because the field
    /// is running slowly, so under those the circuit's typical SC/VSC values are used. Without
    /// circuit data, the green-flag estimate is scaled by the usual ratio (about 65% and 70%).
    /// </summary>
    /// <param name="trackStatus">The feed's track status code: 4 is a safety car, 6 and 7 a VSC.</param>
    public static PitLossEstimate ForTrackStatus(
        PitLossEstimate green,
        PitLossTimes? circuit,
        string? trackStatus
    ) =>
        trackStatus switch
        {
            "4" => new(
                circuit?.SafetyCar ?? green.Seconds * 0.65,
                green.Samples,
                PitLossSource.SafetyCar
            ),
            "6" or "7" => new(
                circuit?.VirtualSafetyCar ?? green.Seconds * 0.7,
                green.Samples,
                PitLossSource.VirtualSafetyCar
            ),
            _ => green,
        };

    /// <summary>
    /// Predicts where a car would rejoin if it made a pit stop now, assuming no one else stops.
    /// Returns <c>null</c> if the car's gap to the leader isn't known.
    /// </summary>
    /// <param name="lines">The current timing lines, keyed by driver number.</param>
    public static RejoinPrediction? PredictRejoin(
        Dictionary<string, TimingDataPoint.Driver> lines,
        string driverNumber,
        double pitLossSeconds
    )
    {
        var ownGap = (double?)lines.SmartGapToLeaderSeconds(driverNumber);
        if (ownGap is null)
            return null;

        var projected = ownGap.Value + pitLossSeconds;

        var others = lines
            .Where(x => x.Key != driverNumber)
            .Where(x => x.Value.Retired != true && x.Value.Stopped != true)
            .Select(x => (DriverNumber: x.Key, Gap: (double?)lines.SmartGapToLeaderSeconds(x.Key)))
            .Where(x => x.Gap.HasValue)
            .Select(x => (x.DriverNumber, Gap: x.Gap!.Value))
            .OrderBy(x => x.Gap)
            .ToList();

        var (aheadNumber, aheadGap) = others.LastOrDefault(x => x.Gap <= projected);
        var (behindNumber, behindGap) = others.FirstOrDefault(x => x.Gap > projected);
        var position = others.Count(x => x.Gap <= projected) + 1;

        return new RejoinPrediction(
            Position: position,
            AheadDriverNumber: aheadNumber,
            GapToAhead: aheadNumber is null ? null : projected - aheadGap,
            BehindDriverNumber: behindNumber,
            GapToBehind: behindNumber is null ? null : behindGap - projected
        );
    }

    /// <summary>
    /// The gap between two drivers (positive when <paramref name="behind"/> is behind
    /// <paramref name="ahead"/>) at the end of each of the last <paramref name="laps"/> laps.
    /// </summary>
    public static List<(int Lap, double Gap)> GetGapHistory(
        IReadOnlyDictionary<int, Dictionary<string, TimingDataPoint.Driver>> driversByLap,
        string ahead,
        string behind,
        int laps
    )
    {
        var result = new List<(int Lap, double Gap)>();
        foreach (var (lap, lines) in driversByLap.OrderByDescending(x => x.Key).Take(laps))
        {
            var aheadGap = lines.SmartGapToLeaderSeconds(ahead);
            var behindGap = lines.SmartGapToLeaderSeconds(behind);
            if (aheadGap.HasValue && behindGap.HasValue)
            {
                result.Add((lap, (double)(behindGap.Value - aheadGap.Value)));
            }
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Least-squares slope of the samples, i.e. the change per lap.
    /// Returns <c>null</c> if there are fewer than 2 samples.
    /// </summary>
    public static double? Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
            return null;

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var denominator = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return denominator == 0
            ? null
            : points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / denominator;
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static IEnumerable<List<int>> ConsecutiveRuns(IEnumerable<int> values)
    {
        var run = new List<int>();
        foreach (var value in values)
        {
            if (run.Count > 0 && value != run[^1] + 1)
            {
                yield return run;
                run = [];
            }
            run.Add(value);
        }
        if (run.Count > 0)
            yield return run;
    }
}
