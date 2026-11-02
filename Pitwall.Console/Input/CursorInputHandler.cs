namespace Pitwall.Console;

public sealed class CursorInputHandler(State state) : IInputHandler
{
    public bool IsEnabled => true;

    // On most pit wall pages the up and down arrows change team instead
    public Screen[] ApplicableScreens =>
        Enum.GetValues<Screen>().Except(ChangeTeamInputHandler.Screens).ToArray();

    public ConsoleKey[] Keys =>
        [ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.J, ConsoleKey.K];

    public ConsoleKey[] DisplayKeys => [ConsoleKey.UpArrow, ConsoleKey.DownArrow];

    public string Description => "Move";

    public int Sort => 21;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        var changeBy = consoleKeyInfo.Key is ConsoleKey.DownArrow or ConsoleKey.J ? 1 : -1;
        if (consoleKeyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            changeBy *= 5;
        }

        state.CursorOffset += changeBy;

        if (state.CursorOffset < 0)
        {
            state.CursorOffset = 0;
        }

        return Task.CompletedTask;
    }
}
