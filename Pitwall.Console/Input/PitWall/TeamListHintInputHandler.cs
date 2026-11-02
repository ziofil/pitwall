namespace Pitwall.Console;

/// <summary>Shows Enter as the way to open the team list in the pit wall footer.</summary>
public sealed class TeamListHintInputHandler : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => ChangeTeamInputHandler.Screens;

    // Enter is handled by ChangeTeamInputHandler; this only labels it in the footer
    public ConsoleKey[] Keys => [];

    public ConsoleKey[] DisplayKeys => [ConsoleKey.Enter];

    public string Description => "Team List";

    public int Sort => 22;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;
}
