namespace Pitwall.Data;

/// <summary>
/// Continuous versions of each car's gap to the leader and interval to the car ahead. The feed
/// updates these as cars pass timing loops around the lap, so on their own they jump every
/// second or so; this buffers the updates and interpolates between them with a
/// <see cref="SignalSmoother"/>. Values are evaluated <see cref="RenderLag"/> behind the session
/// clock, so they are mostly interpolated rather than predicted. Consumes the same TimingData
/// feed as <see cref="TimingDataProcessor"/>.
/// </summary>
public sealed class TimingSmoother(IDateTimeProvider dateTimeProvider) : IProcessor<TimingDataPoint>
{
    /// <summary>How far behind the session clock smoothed values are shown.</summary>
    public static readonly TimeSpan RenderLag = TimeSpan.FromSeconds(1.5);

    // Gaps change by tenths between loops; a jump of seconds is a pit stop, an incident or a
    // change of car ahead, and should show as a jump.
    // The feed updates each car's gaps about every 4 seconds (90% within 6), so follow the trend
    // for up to 6 seconds past the newest update, and ease on to the corrected curve over a
    // second when the next update arrives.
    private readonly SignalSmoother _smoother = new(
        window: TimeSpan.FromSeconds(30),
        maxExtrapolation: TimeSpan.FromSeconds(6),
        resetThreshold: 2.0,
        blend: TimeSpan.FromSeconds(1)
    );

    public TimingDataPoint Latest { get; private set; } = new();

    public void Process(TimingDataPoint data)
    {
        Latest = data;
        var now = dateTimeProvider.Utc;
        foreach (var (driverNumber, line) in data.Lines)
        {
            if (line.GapToLeader is not null)
                Record(GapKey(driverNumber), line.GapToLeaderSeconds(), now);
            if (line.IntervalToPositionAhead?.Value is not null)
                Record(
                    IntervalKey(driverNumber),
                    line.IntervalToPositionAhead.IntervalSeconds(),
                    now
                );
        }
    }

    /// <summary>The smoothed gap to the leader in seconds, or <c>null</c> if it isn't a time.</summary>
    public double? GapToLeader(string driverNumber) => Get(GapKey(driverNumber));

    /// <summary>The smoothed interval to the car ahead in seconds, or <c>null</c> if it isn't a time.</summary>
    public double? Interval(string driverNumber) => Get(IntervalKey(driverNumber));

    private void Record(string key, decimal? value, DateTimeOffset now)
    {
        // Lapped cars ("1L") and the leader's lap count have no time to smooth
        if (value is null)
            _smoother.Reset(key);
        else
            _smoother.Add(key, now, (double)value.Value);
    }

    private double? Get(string key) =>
        _smoother.TryGet(key, dateTimeProvider.Utc - RenderLag, out var value)
            ? Math.Max(0, value)
            : null;

    private static string GapKey(string driverNumber) => $"{driverNumber}:gap";

    private static string IntervalKey(string driverNumber) => $"{driverNumber}:interval";
}
