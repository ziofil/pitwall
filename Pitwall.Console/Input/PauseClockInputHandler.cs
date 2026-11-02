using Pitwall.Data;

namespace Pitwall.Console;

public class PauseClockInputHandler(IDateTimeProvider dateTimeProvider) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens =>
        [
            Screen.PitWallTiming,
            Screen.PitWallStrategy,
            Screen.PitWallTelemetry,
            Screen.PitWallTrackMap,
            Screen.PitWallComms,
        ];

    public ConsoleKey[] Keys => [ConsoleKey.P];

    public string Description =>
        dateTimeProvider.IsPaused ? "[olive]Resume Clock[/]" : "Pause Clock";

    public int Sort => 23;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        dateTimeProvider.TogglePause();

        return Task.CompletedTask;
    }
}
