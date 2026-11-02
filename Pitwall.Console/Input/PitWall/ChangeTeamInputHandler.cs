namespace Pitwall.Console;

/// <summary>On the pit wall pages, Tab jumps to the next team and G opens the team list.</summary>
public sealed class ChangeTeamInputHandler(State state, PitWallContext context) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => PitWallComponents.Pages.Select(x => x.Screen).ToArray();

    public ConsoleKey[] Keys => [ConsoleKey.Tab, ConsoleKey.G];

    public string Description => "Next Team / List";

    public int Sort => 62;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        if (consoleKeyInfo.Key == ConsoleKey.Tab)
        {
            context.CycleTeam();
            state.CursorOffset = 0;
        }
        else
        {
            state.CurrentScreen = Screen.SelectTeam;
            state.CursorOffset = Math.Max(
                0,
                context.Teams.ToList().FindIndex(x => x.Name == state.SelectedTeam)
            );
        }
    }
}
