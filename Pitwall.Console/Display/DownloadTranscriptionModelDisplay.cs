using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public class DownloadTranscriptionModelDisplay(ITranscriptionProvider transcriptionProvider)
    : IDisplay
{
    public Screen Screen => Screen.DownloadTranscriptionModel;

    public Task<IRenderable> GetContentAsync()
    {
        var text = $"""
            Team radio transcription uses a Whisper speech recognition model that runs entirely on this machine:
            no audio or text is sent anywhere.

            The model is a one-time download of about [bold]550 MB[/], stored at {Markup.Escape(
                transcriptionProvider.ModelPath
            )}
            (inside the data directory; run [bold]pitwall info[/] to see where that is).

            Press [bold]Enter[/] to download it, or [bold]Esc[/] to go back.

            Once it's downloaded, the selected team's radio messages are transcribed automatically on the Comms page.

            """;

        var downloadProgress = Math.Ceiling(transcriptionProvider.DownloadProgress * 100);

        IRenderable rows =
            downloadProgress == 0
                ? new Markup(text)
                : new Rows(
                    new Markup(text),
                    new BarChart()
                        .AddItem("Download Progress (%)", downloadProgress, Color.Green)
                        .WithMaxValue(100)
                );

        return Task.FromResult<IRenderable>(new Panel(rows).Expand());
    }
}
