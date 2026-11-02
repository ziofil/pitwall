using Pitwall.Data;

namespace Pitwall.Console;

public class StartLiveSessionInputHandler(
    SessionInfoProcessor sessionInfo,
    ILiveTimingClient liveTimingClient,
    State state
) : IInputHandler
{
    public bool IsEnabled => sessionInfo.Latest.Name is null;

    public Screen[] ApplicableScreens => [Screen.ManageSession];

    public ConsoleKey[] Keys => [ConsoleKey.L];

    public string Description => "Start Live Session";

    public int Sort => 40;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        await liveTimingClient.StartAsync();
        // Go straight to the pit wall, asking for a team first if none is chosen yet
        state.CurrentScreen = state.SelectedTeam is null ? Screen.SelectTeam : Screen.PitWallTiming;
    }
}
