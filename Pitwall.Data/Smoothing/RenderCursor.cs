namespace Pitwall.Data;

/// <summary>
/// The moment to draw a feed at, for feeds stamped with their own clock (car telemetry and
/// positions). Live, the wall clock runs ahead of feed time by a delivery delay that varies, so
/// the cursor isn't derived from the wall clock: it advances with real elapsed time, aiming to
/// stay a delay behind the newest sample. It never runs past the newest sample or backwards.
/// </summary>
/// <remarks>
/// <para>
/// When it drifts away from that aim, e.g. pinned to the newest sample after the feed paused, it
/// plays slightly slower or faster until it's back, rather than jumping. Within
/// <see cref="Deadband"/> of the aim it runs at exactly real time, so the newest sample's
/// once-a-second steps don't modulate the speed. Only a lag beyond <c>maxLag</c> jumps.
/// </para>
/// <para>
/// The delay adapts to the connection, like a video call's jitter buffer: every time the cursor
/// catches up with the newest sample and has to stop, the delay grows a little; while it runs
/// smoothly, it slowly shrinks back towards <c>minDelay</c>.
/// </para>
/// </remarks>
public sealed class RenderCursor(
    IDateTimeProvider dateTimeProvider,
    TimeSpan minDelay,
    TimeSpan maxLag
)
{
    private static readonly TimeSpan Deadband = TimeSpan.FromSeconds(0.5);

    /// <summary>Speed change per second of error beyond the deadband.</summary>
    private const double CatchUpRate = 0.25;

    /// <summary>How much the delay grows each time the cursor has to stop.</summary>
    private static readonly TimeSpan StallStep = TimeSpan.FromSeconds(0.25);

    /// <summary>How fast the delay shrinks back while nothing stalls, in seconds per second.</summary>
    private const double Recovery = 0.005;

    private readonly TimeSpan _minDelay = minDelay;
    private readonly TimeSpan _maxDelay = maxLag - TimeSpan.FromSeconds(1);

    private long _newestTicks;
    private DateTimeOffset _cursor;
    private DateTimeOffset _lastWall;
    private bool _started;
    private bool _stalled;

    /// <summary>How far behind the newest sample the cursor currently aims to be.</summary>
    public TimeSpan Delay { get; private set; } = minDelay;

    /// <summary>Records the newest sample time of a batch, from the thread receiving the feed.</summary>
    public void Observe(DateTimeOffset newest)
    {
        var current = Volatile.Read(ref _newestTicks);
        // Much older than the newest means the feed restarted earlier, e.g. a replay seeking back
        if (newest.UtcTicks > current || newest.UtcTicks < current - maxLag.Ticks)
            Volatile.Write(ref _newestTicks, newest.UtcTicks);
    }

    /// <summary>
    /// The time to draw at this frame, or <c>null</c> before any samples. Call from the render
    /// thread; calls later in the same frame see no elapsed time, so they all get the same moment.
    /// </summary>
    public DateTimeOffset? Advance()
    {
        var ticks = Volatile.Read(ref _newestTicks);
        if (ticks == 0)
            return null;

        var newest = new DateTimeOffset(ticks, TimeSpan.Zero);
        var now = dateTimeProvider.Utc;
        if (!_started || _cursor < newest - maxLag)
        {
            _cursor = newest - Delay;
            _started = true;
        }
        else if (now > _lastWall)
        {
            var elapsed = now - _lastWall;
            var shrunk = Delay - (elapsed * Recovery);
            Delay = shrunk > _minDelay ? shrunk : _minDelay;

            var error = (newest - Delay - _cursor).TotalSeconds;
            var beyond = Math.Max(0, Math.Abs(error) - Deadband.TotalSeconds) * Math.Sign(error);
            var speed = Math.Max(0, 1 + (CatchUpRate * beyond));
            _cursor += elapsed * speed;

            // Caught up with the newest sample: the data came later than the delay allows for
            var stalled = _cursor >= newest;
            if (stalled && !_stalled && Delay + StallStep <= _maxDelay)
                Delay += StallStep;
            _stalled = stalled;
        }
        if (_cursor > newest)
            _cursor = newest;
        _lastWall = now;
        return _cursor;
    }
}
