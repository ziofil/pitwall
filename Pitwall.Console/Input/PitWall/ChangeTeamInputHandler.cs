namespace Pitwall.Console;

/// <summary>
/// Tab and Shift+Tab switch to the next or previous team on every pit wall page. On the pages
/// without a list to scroll, ▲/▼ do the same and Enter opens the team list.
/// </summary>
public sealed class ChangeTeamInputHandler(State state, PitWallContext context) : IInputHandler
{
    /// <summary>The pit wall pages where the up and down arrows change team.</summary>
    public static readonly Screen[] Screens =
    [
        Screen.PitWallTiming,
        Screen.PitWallStrategy,
        Screen.PitWallTelemetry,
        Screen.PitWallTrackMap,
        Screen.PitWallWeather,
    ];

    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => PitWallComponents.Pages.Select(x => x.Screen).ToArray();

    // On Comms the arrows and Enter pick and play radio messages, so only Tab changes team there
    public ConsoleKey[] Keys =>
        Screens.Contains(state.CurrentScreen)
            ?
            [
                ConsoleKey.Tab,
                ConsoleKey.UpArrow,
                ConsoleKey.K,
                ConsoleKey.DownArrow,
                ConsoleKey.J,
                ConsoleKey.Enter,
            ]
            : [ConsoleKey.Tab];

    public ConsoleKey[] DisplayKeys =>
        Screens.Contains(state.CurrentScreen)
            ? [ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.Tab]
            : [ConsoleKey.Tab];

    public string Description => "Team";

    public int Sort => 21;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        switch (consoleKeyInfo.Key)
        {
            case ConsoleKey.Enter:
                state.CurrentScreen = Screen.SelectTeam;
                state.CursorOffset = Math.Max(
                    0,
                    context.Teams.ToList().FindIndex(x => x.Name == state.SelectedTeam)
                );
                break;
            case ConsoleKey.UpArrow or ConsoleKey.K:
            case ConsoleKey.Tab when consoleKeyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                context.CycleTeam(-1);
                break;
            default:
                context.CycleTeam(1);
                break;
        }
    }
}
