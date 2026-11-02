namespace Pitwall.Console;

/// <summary>Picks the team under the cursor on the team selection screen.</summary>
public sealed class SelectTeamInputHandler(State state, PitWallContext context) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.SelectTeam];

    public ConsoleKey[] Keys => [ConsoleKey.Enter];

    public string Description => "Select Team";

    public int Sort => 40;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        var team = context.Teams.ElementAtOrDefault(state.CursorOffset);
        if (team is null)
            return;

        state.SelectedTeam = team.Name;
        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);

        // Return to the pit wall page the team was changed from, or start on the timing page
        var previous = state.PreviousScreen;
        state.CurrentScreen = PitWallComponents.Pages.Any(x => x.Screen == previous)
            ? previous
            : Screen.PitWallTiming;
        state.CursorOffset = 0;
    }
}
