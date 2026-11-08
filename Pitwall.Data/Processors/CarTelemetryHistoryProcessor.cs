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
public sealed record TelemetryLap(int Lap, IReadOnlyList<TelemetrySample> Samples)
{
    public double Distance => Samples.Count == 0 ? 0 : Samples[^1].Distance;
}

/// <summary>
/// Keeps a short per-lap history of car telemetry (speed, RPM, gear, throttle, brake, DRS),
/// so that traces can be drawn against lap distance and compared lap-on-lap.
/// <see cref="CarDataProcessor"/> only keeps the newest sample, which is enough for live readouts
/// but not for traces. Distance isn't in the feed, so it's integrated from speed.
/// </summary>
public sealed class CarTelemetryHistoryProcessor(
    TimingDataProcessor timingData,
    IDateTimeProvider dateTimeProvider
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

        // NumberOfLaps is the count of completed laps, so the lap being driven is one more.
        var lap = (timingData.Latest.Lines.GetValueOrDefault(driverNumber)?.NumberOfLaps ?? 0) + 1;

        if (lap != state.CurrentLap)
        {
            if (state.Current.Count > 0)
            {
                state.Completed.Add(new TelemetryLap(state.CurrentLap, state.Current.ToArray()));
                if (state.Completed.Count > CompletedLapsToKeep)
                {
                    state.Completed.RemoveAt(0);
                }
            }
            state.Current = [];
            state.CurrentLap = lap;
            state.LapStartUtc = utc;
            state.Distance = 0;
        }
        else
        {
            var dt = utc - state.LastUtc;
            if (dt <= MaxSampleGap && state.Current.Count > 0)
            {
                // Trapezoidal integration of speed (km/h) over the interval, in metres.
                var previousSpeed = state.Current[^1].Speed;
                var speed = channels.Speed ?? previousSpeed;
                state.Distance += (previousSpeed + speed) / 2.0 / 3.6 * dt.TotalSeconds;
            }
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
