using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// Space syncs the pit wall to a TV broadcast: pressed the moment the session goes green on
/// screen (lights out, or the start of a qualifying part or a restart), it sets the stream delay
/// so the pit wall's clock is at that start now. The start used is the one nearest the pit
/// wall's clock, so it works with or without a delay already set.
/// </summary>
public class SyncToStartInputHandler(
    IDateTimeProvider dateTimeProvider,
    SessionStarts sessionStarts,
    State state
) : IInputHandler
{
    // Only shown when there's a start to sync to
    public bool IsEnabled => !dateTimeProvider.IsPaused && sessionStarts.All.Count > 0;

    public Screen[] ApplicableScreens =>
        [
            Screen.PitWallTiming,
            Screen.PitWallStrategy,
            Screen.PitWallTelemetry,
            Screen.PitWallTrackMap,
            Screen.PitWallComms,
            Screen.PitWallWeather,
        ];

    public ConsoleKey[] Keys => [ConsoleKey.Spacebar];

    public string Description => "Sync at the start";

    public int Sort => 21;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        Sync(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the delay so the session clock is at the nearest start at <paramref name="now"/>.
    /// The delay can't go below zero: the pit wall can't run ahead of the live feed.
    /// </summary>
    internal bool Sync(DateTimeOffset now)
    {
        if (sessionStarts.Nearest(dateTimeProvider.Utc) is not { } start)
            return false;
        dateTimeProvider.Delay = now - start > TimeSpan.Zero ? now - start : TimeSpan.Zero;
        state.DelayChangedAt = DateTimeOffset.UtcNow;
        return true;
    }
}
