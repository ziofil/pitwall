namespace Pitwall.Console;

public class SwitchToDriverSelectInputHandler(State state) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.DriverTracker, Screen.TimingHistory];

    public ConsoleKey[] Keys => [ConsoleKey.D];

    public string Description => "Select Drivers";

    public int Sort => 69;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        state.CurrentScreen = Screen.SelectDriver;
        state.CursorOffset = 1;
    }
}
