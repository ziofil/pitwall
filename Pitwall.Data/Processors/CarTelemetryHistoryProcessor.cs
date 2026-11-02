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

    private sealed class DriverState
    {
        public readonly List<TelemetryLap> Completed = [];
        public List<TelemetrySample> Current = [];
        public int CurrentLap;
        public DateTimeOffset LapStartUtc;
        public DateTimeOffset LastUtc;
        public double Distance;
    }

    private readonly ConcurrentDictionary<string, DriverState> _drivers = new();

    private static readonly TimeSpan Blend = TimeSpan.FromSeconds(0.3);

    // Continuous versions of the analogue channels, for the live readouts. Car data arrives in
    // batches about once a second; this interpolates between the samples.
    private readonly SignalSmoother _speed = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(0.5),
        resetThreshold: 120,
        blend: Blend
    );
    private readonly SignalSmoother _rpm = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(0.5),
        resetThreshold: 6000,
        blend: Blend
    );
    private readonly SignalSmoother _throttle = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(0.5),
        resetThreshold: 100,
        blend: Blend
    );
    private readonly SignalSmoother _distance = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(1),
        resetThreshold: 500,
        blend: Blend
    );

    public CarDataPoint Latest { get; private set; } = new();

    public void Process(CarDataPoint data)
    {
        Latest = data;
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
    /// The car's telemetry as it was <see cref="TimingSmoother.RenderLag"/> ago on the session
    /// clock: speed, RPM, throttle and lap distance interpolated between samples, gear, brake and
    /// DRS from the sample at that moment.
    /// </summary>
    public bool TryGetLive(string driverNumber, out TelemetrySample sample)
    {
        sample = default;
        if (!_drivers.TryGetValue(driverNumber, out var state))
            return false;

        var at = dateTimeProvider.Utc - TimingSmoother.RenderLag;
        TelemetrySample? exact = null;
        lock (state)
        {
            // The newest sample at or before the moment shown, for the step channels
            for (var i = state.Current.Count - 1; i >= 0; i--)
            {
                if (state.LapStartUtc + state.Current[i].Elapsed <= at)
                {
                    exact = state.Current[i];
                    break;
                }
            }
            exact ??= state.Current.Count > 0 ? state.Current[0] : null;
        }
        if (exact is not { } step)
            return false;

        sample = step with
        {
            Speed = _speed.TryGet(driverNumber, at, out var speed)
                ? (int)Math.Round(speed)
                : step.Speed,
            Rpm = _rpm.TryGet(driverNumber, at, out var rpm) ? (int)Math.Round(rpm) : step.Rpm,
            Throttle = _throttle.TryGet(driverNumber, at, out var throttle)
                ? (int)Math.Clamp(Math.Round(throttle), 0, 100)
                : step.Throttle,
            Distance = _distance.TryGet(driverNumber, at, out var distance)
                ? Math.Max(0, distance)
                : step.Distance,
        };
        return true;
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
        _speed.Add(driverNumber, utc, channels.Speed ?? 0);
        _rpm.Add(driverNumber, utc, channels.Rpm ?? 0);
        _throttle.Add(driverNumber, utc, Math.Clamp(channels.Throttle ?? 0, 0, 100));
        _distance.Add(driverNumber, utc, state.Distance);
        state.Current.Add(
            new TelemetrySample(
                Distance: state.Distance,
                Elapsed: utc - state.LapStartUtc,
                Speed: channels.Speed ?? 0,
                Rpm: channels.Rpm ?? 0,
                Gear: channels.Ngear ?? 0,
                Throttle: Math.Clamp(channels.Throttle ?? 0, 0, 100),
                Brake: channels.Brake ?? 0,
                DrsOpen: channels.Drs is 10 or 12 or 14
            )
        );
    }
}
