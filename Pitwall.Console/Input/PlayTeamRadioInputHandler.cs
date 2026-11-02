using Pitwall.Console.Audio;
using Pitwall.Data;

namespace Pitwall.Console;

public sealed class PlayTeamRadioInputHandler(
    AudioPlayer audioPlayer,
    State state,
    TeamRadioProcessor teamRadio,
    PitWallContext pitWallContext
) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.PitWallComms];

    public ConsoleKey[] Keys => [ConsoleKey.Enter];

    public string Description =>
        audioPlayer.Playing ? "[olive]⏹ Stop[/]"
        : audioPlayer.Errored ? "[red]Playback Error[/]"
        : "► Play Radio";

    public int Sort => 40;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        if (audioPlayer.Playing)
        {
            audioPlayer.Stop();
        }
        else
        {
            await PlayAsync(state.CursorOffset, cancellationToken);
        }
    }

    private async Task PlayAsync(int offset, CancellationToken cancellationToken = default)
    {
        // The comms page only lists the selected team's radio
        var radio = PitWallCommsDisplay
            .TeamCaptures(teamRadio, pitWallContext)
            .ElementAtOrDefault(offset);
        if (radio.Key is null)
            return;
        var destFileName = await teamRadio.DownloadTeamRadioToFileAsync(
            radio.Key,
            cancellationToken
        );
        audioPlayer.Play(destFileName);
    }
}
