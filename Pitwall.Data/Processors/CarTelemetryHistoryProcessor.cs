using System.Collections.Concurrent;

namespace Pitwall.Data;

/// <summary>
/// One telemetry sample, positioned by distance travelled since the start of the lap.
/// </summary>
/// <param name="Distance">Metres since the start of the lap, integrated from speed.</param>
/// <param name="Elapsed">Time since the start of the lap.</param>
public readonly record struct TelemetrySample(
    double Distance,
    TimeSpan Elapsed,
    int Speed,
    int Rpm,
    int Gear,
    int Throttle,
    int Brake,
    bool DrsOpen
);

/// <summary>
/// The telemetry recorded for one lap of one car.
/// </summary>
/// <param name="Lap">The lap number being driven (1 is the first lap of the session).</param>
/// <param name="Duration">
/// For a completed lap, the time from its start to the next lap's: its lap time, near enough
/// (the timing updates that mark both ends trail the line by about the same).
/// </param>
public sealed record TelemetryLap(
    int Lap,
    IReadOnlyList<TelemetrySample> Samples,
    TimeSpan? Duration = null
)
{
    public double Distance => Samples.Count == 0 ? 0 : Samples[^1].Distance;
}

/// <summary>
/// Keeps a short per-lap history of car telemetry (speed, RPM, gear, throttle, brake, DRS),
/// so that traces can be drawn against lap distance and compared lap-on-lap, and each car's
/// best lap for the live delta to it. <see cref="CarDataProcessor"/> only keeps the newest
/// sample, which is enough for live readouts but not for traces. Distance isn't in the feed, so
/// it's integrated from speed. Samples are split into laps by their own time against when the
/// timing feed counted each lap (<see cref="LapCrossingProcessor"/>), as telemetry arrives a
/// second or two after the timing for the same moment.
/// </summary>
public sealed class CarTelemetryHistoryProcessor(
    TimingDataProcessor timingData,
    IDateTimeProvider dateTimeProvider,
    LapCrossingProcessor? crossings = null
) : IProcessor<CarDataPoint>
{
    /// <summary>How many completed laps are kept per driver, in addition to the current lap.</summary>
    public const int CompletedLapsToKeep = 3;

    // Samples further apart than this are treated as a gap in the feed, not integrated across.
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2);

    // The live values are fitted through this many samples nearest the moment shown
    private const int FitPoints = 4;

    // Recent samples kept for the live values: comfortably more than the render delay covers
    private const int RecentCapacity = 48;

    private sealed class DriverState
    {
        public readonly List<TelemetryLap> Completed = [];

        // The newest samples with their feed time and lap, across lap boundaries
        public readonly List<(DateTimeOffset Utc, int Lap, TelemetrySample Sample)> Recent = [];
        public List<TelemetrySample> Current = [];
        public int CurrentLap;
        public DateTimeOffset LapStartUtc;
        public DateTimeOffset LastUtc;
        public double Distance;
    }

    private readonly ConcurrentDictionary<string, DriverState> _drivers = new();

    // Feed time to show the live values at. Not the session clock minus a lag: live, the samples
    // arrive a varying 2-3s late, so that would run past the newest sample and stutter. Car data
    // arrives in batches about once a second; the live values are interpolated between samples
    private readonly RenderCursor _cursor = new(
        dateTimeProvider,
        TimingSmoother.RenderLag,
        TimeSpan.FromSeconds(5)
    );

    public CarDataPoint Latest { get; private set; } = new();

    public void Process(CarDataPoint data)
    {
        Latest = data;
        if (data.Entries.Count > 0)
            _cursor.Observe(data.Entries.Max(x => x.Utc));
        foreach (var entry in data.Entries.OrderBy(x => x.Utc))
        {
            foreach (var (driverNumber, car) in entry.Cars)
            {
                var state = _drivers.GetOrAdd(driverNumber, _ => new DriverState());
                lock (state)
                {
                    AddSample(driverNumber, state, entry.Utc, car.Channels);
                }
            }
        }
    }

    /// <summary>
    /// The car's telemetry a moment behind the newest sample (see <see cref="RenderCursor"/>):
    /// speed, RPM, throttle and lap distance fitted through the samples nearest that moment, so
    /// they move smoothly despite the feed's uneven sample times; gear, brake and DRS from the
    /// sample at that moment.
    /// </summary>
    public bool TryGetLive(string driverNumber, out TelemetrySample sample)
    {
        sample = default;
        if (!_drivers.TryGetValue(driverNumber, out var state))
            return false;
        if (_cursor.Advance() is not { } at)
            return false;

        (DateTimeOffset Utc, int Lap, TelemetrySample Sample)[] recent;
        lock (state)
            recent = [.. state.Recent];
        if (recent.Length == 0)
            return false;

        // The newest sample at or before the moment shown, for the step channels; the fit only
        // uses samples from the same lap, as distance starts again from zero each lap
        var index = Math.Max(0, Array.FindLastIndex(recent, x => x.Utc <= at));
        var (_, lap, step) = recent[index];
        var nearest = recent
            .Where(x => x.Lap == lap)
            .OrderBy(x => (x.Utc - at).Duration())
            .Take(FitPoints)
            .ToArray();
        double Fit(Func<TelemetrySample, double> value) => FitAt(nearest, at, value);

        sample = step with
        {
            Elapsed = TimeSpan.FromSeconds(Math.Max(0, Fit(x => x.Elapsed.TotalSeconds))),
            Speed = (int)Math.Round(Fit(x => x.Speed)),
            Rpm = (int)Math.Round(Fit(x => x.Rpm)),
            Throttle = (int)Math.Clamp(Math.Round(Fit(x => x.Throttle)), 0, 100),
            Distance = Math.Max(0, Fit(x => x.Distance)),
        };
        return true;
    }

    /// <summary>A straight line fitted through the samples, evaluated at the given time.</summary>
    private static double FitAt(
        (DateTimeOffset Utc, int Lap, TelemetrySample Sample)[] samples,
        DateTimeOffset at,
        Func<TelemetrySample, double> value
    )
    {
        // Time measured from the moment shown, so the line's intercept is the value then
        double n = samples.Length,
            st = 0,
            st2 = 0,
            sv = 0,
            stv = 0;
        foreach (var (utc, _, sample) in samples)
        {
            var t = (utc - at).TotalSeconds;
            var v = value(sample);
            (st, st2, sv, stv) = (st + t, st2 + (t * t), sv + v, stv + (t * v));
        }
        var denominator = (n * st2) - (st * st);
        if (Math.Abs(denominator) < 1e-9)
            return sv / n;
        var slope = ((n * stv) - (st * sv)) / denominator;
        return (sv - (slope * st)) / n;
    }

    /// <summary>
    /// Closes the lap in progress and starts the next at <paramref name="start"/>. Samples of the
    /// closing lap taken after that moment (they can arrive before the timing update that ends
    /// the lap) move to the new lap.
    /// </summary>
    private void StartLap(string driverNumber, DriverState state, int lap, DateTimeOffset start)
    {
        var moved = new List<TelemetrySample>();
        if (state.Current.Count > 0)
        {
            var closing = state.Current.Where(x => state.LapStartUtc + x.Elapsed < start).ToList();
            var after = state.Current.Skip(closing.Count).ToList();
            // Where the car was on the closing lap at the crossing, so the moved samples measure
            // from the line
            var atLine = after.Count == 0 ? 0 : after[0].Distance;
            if (closing.Count > 0 && after.Count > 0)
            {
                var (a, b) = (closing[^1], after[0]);
                var (ta, tb) = (state.LapStartUtc + a.Elapsed, state.LapStartUtc + b.Elapsed);
                var f = tb > ta ? (start - ta) / (tb - ta) : 0;
                atLine = a.Distance + ((b.Distance - a.Distance) * f);
            }
            foreach (var sample in after)
            {
                moved.Add(
                    sample with
                    {
                        Distance = Math.Max(0, sample.Distance - atLine),
                        Elapsed = state.LapStartUtc + sample.Elapsed - start,
                    }
                );
            }
            if (closing.Count > 0)
                state.Completed.Add(
                    new TelemetryLap(state.CurrentLap, [.. closing], start - state.LapStartUtc)
                );
            Prune(driverNumber, state);

            // The live values fit through recent samples of one lap, so relabel the moved ones
            for (var i = 0; i < state.Recent.Count; i++)
            {
                var (utc, recentLap, sample) = state.Recent[i];
                if (recentLap == state.CurrentLap && utc >= start)
                {
                    var rebased = moved.FirstOrDefault(x => x.Elapsed == utc - start);
                    state.Recent[i] = (utc, lap, rebased == default ? sample : rebased);
                }
            }
        }
        state.Current = moved;
        state.CurrentLap = lap;
        state.LapStartUtc = start;
        state.Distance = moved.Count == 0 ? 0 : moved[^1].Distance;
    }

    /// <summary>
    /// Drops the oldest completed laps beyond <see cref="CompletedLapsToKeep"/>, but never the
    /// car's best lap of the session, which the live delta is measured against.
    /// </summary>
    private void Prune(string driverNumber, DriverState state)
    {
        var best = BestOf(driverNumber, state.Completed);
        while (state.Completed.Count(x => x != best) > CompletedLapsToKeep)
            state.Completed.Remove(state.Completed.First(x => x != best));
    }

    /// <summary>The telemetry of the car's best lap this session, if it's held.</summary>
    public TelemetryLap? GetBestLap(string driverNumber)
    {
        if (!_drivers.TryGetValue(driverNumber, out var state))
            return null;
        lock (state)
            return BestOf(driverNumber, state.Completed);
    }

    // A lap whose duration is within this of the official best lap time is that lap
    private static readonly TimeSpan BestLapMatch = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Which of the laps is the car's official best: the one that took that long. Lap numbers
    /// can't say, as the feed counts them differently in qualifying (leaving the pits counts as
    /// a lap there) and in races.
    /// </summary>
    private TelemetryLap? BestOf(string driverNumber, List<TelemetryLap> laps)
    {
        var best = timingData.Latest.Lines.GetValueOrDefault(driverNumber)?.BestLapTime.ToTimeSpan();
        return best is null
            ? null
            : laps.Where(x => x.Duration is { } d && (d - best.Value).Duration() < BestLapMatch)
                .MinBy(x => (x.Duration!.Value - best.Value).Duration());
    }

    /// <summary>
    /// Returns the laps held for the given driver, oldest first. The last entry is the lap in progress.
    /// </summary>
    public IReadOnlyList<TelemetryLap> GetLaps(string driverNumber)
    {
        if (!_drivers.TryGetValue(driverNumber, out var state))
            return [];

        lock (state)
        {
            var laps = new List<TelemetryLap>(state.Completed);
            if (state.Current.Count > 0)
            {
                laps.Add(new TelemetryLap(state.CurrentLap, state.Current.ToArray()));
            }
            return laps;
        }
    }

    private void AddSample(
        string driverNumber,
        DriverState state,
        DateTimeOffset utc,
        CarDataPoint.Entry.Car.Channel channels
    )
    {
        if (utc <= state.LastUtc)
            return;

        // The lap the sample belongs to, by its own time; before any lap has been seen counted,
        // the lap count now (NumberOfLaps is the laps completed, so the lap being driven is one
        // more)
        var crossing = crossings?.LapAt(driverNumber, utc);
        var lap =
            crossing?.Lap
            ?? (timingData.Latest.Lines.GetValueOrDefault(driverNumber)?.NumberOfLaps ?? 0) + 1;

        if (lap > state.CurrentLap)
        {
            StartLap(driverNumber, state, lap, crossing?.Start ?? utc);
        }
        else if (lap < state.CurrentLap)
        {
            // A sample from a lap already closed, arriving out of order: too late to place
            return;
        }

        var dt = utc - state.LastUtc;
        if (state.Current.Count > 0 && dt <= MaxSampleGap)
        {
            // Trapezoidal integration of speed (km/h) over the interval, in metres
            var previousSpeed = state.Current[^1].Speed;
            var speed = channels.Speed ?? previousSpeed;
            state.Distance += (previousSpeed + speed) / 2.0 / 3.6 * dt.TotalSeconds;
        }
        else if (state.Current.Count == 0 && utc > state.LapStartUtc)
        {
            // The first sample of a lap: the distance covered since the line
            state.Distance =
                (channels.Speed ?? 0) / 3.6 * Math.Min(MaxSampleGap.TotalSeconds, (utc - state.LapStartUtc).TotalSeconds);
        }

        state.LastUtc = utc;
        var sample = new TelemetrySample(
            Distance: state.Distance,
            Elapsed: utc - state.LapStartUtc,
            Speed: channels.Speed ?? 0,
            Rpm: channels.Rpm ?? 0,
            Gear: channels.Ngear ?? 0,
            Throttle: Math.Clamp(channels.Throttle ?? 0, 0, 100),
            Brake: channels.Brake ?? 0,
            DrsOpen: channels.Drs is 10 or 12 or 14
        );
        state.Current.Add(sample);
        state.Recent.Add((utc, lap, sample));
        if (state.Recent.Count > RecentCapacity)
            state.Recent.RemoveAt(0);
    }
}
