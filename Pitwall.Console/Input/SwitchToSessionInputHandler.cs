namespace Pitwall.Console;

public class SwitchToSessionInputHandler(State state) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens =>
        [Screen.Main, Screen.SelectTeam, .. PitWallComponents.Pages.Select(x => x.Screen)];

    public ConsoleKey[] Keys => [ConsoleKey.S];

    public string Description => "Session";

    public int Sort => 60;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        state.CurrentScreen = Screen.ManageSession;
    }
}
