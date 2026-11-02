namespace Pitwall.Console;

/// <summary>Opens the pit wall pages, asking for a team first if none is selected yet.</summary>
public sealed class SwitchToPitWallInputHandler(State state, PitWallContext context) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.Main, Screen.ManageSession];

    public ConsoleKey[] Keys => [ConsoleKey.W];

    public string Description => "Pit Wall";

    public int Sort => 60;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        state.CurrentScreen = context.Team is null ? Screen.SelectTeam : Screen.PitWallTiming;
        state.CursorOffset = 0;
    }
}
