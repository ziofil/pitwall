using Instances.Exceptions;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// Transcribes the selected team's radio messages in the background, newest first, one at a time,
/// using the local Whisper model. Nothing is sent anywhere: the audio comes from F1's feed and is
/// transcribed on this machine. Does nothing until the model has been downloaded.
/// </summary>
public sealed class RadioTranscriber(
    TeamRadioProcessor teamRadio,
    PitWallContext context,
    ITranscriptionProvider transcriptionProvider,
    ILogger<RadioTranscriber> logger
)
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _failed = [];
    private Task? _worker;

    /// <summary>The capture being transcribed right now, if any.</summary>
    public string? Current { get; private set; }

    /// <summary>
    /// Set when transcription can't work on this machine (e.g. FFmpeg isn't installed), after
    /// which no more attempts are made.
    /// </summary>
    public string? Problem { get; private set; }

    public bool ModelAvailable => transcriptionProvider.IsModelDownloaded;

    /// <summary>
    /// Whether every one of the team's messages has been transcribed (or couldn't be), so there
    /// is nothing left to do until new messages arrive.
    /// </summary>
    public bool Finished
    {
        get
        {
            lock (_lock)
                return (_worker is null or { IsCompleted: true })
                    && (Problem is not null || NextCapture() is null);
        }
    }

    /// <summary>
    /// Starts transcribing any of the team's messages that don't have a transcription yet.
    /// Cheap to call often: does nothing while already working or when there's nothing to do.
    /// </summary>
    public void Tick()
    {
        if (!ModelAvailable || Problem is not null)
            return;

        lock (_lock)
        {
            if (_worker is { IsCompleted: false } || NextCapture() is null)
                return;
            _worker = Task.Run(RunAsync);
        }
    }

    private string? NextCapture() =>
        PitWallCommsDisplay
            .TeamCaptures(teamRadio, context)
            .Where(x => x.Value.Transcription is null && !_failed.Contains(x.Key))
            .Select(x => x.Key)
            .FirstOrDefault();

    private async Task RunAsync()
    {
        while (Problem is null && NextCapture() is { } key)
        {
            Current = key;
            try
            {
                await teamRadio.TranscribeAsync(key);
            }
            catch (InstanceFileNotFoundException ex)
            {
                logger.LogError(ex, "Transcription failed, FFmpeg not found");
                Problem =
                    "Transcription needs FFmpeg, which wasn't found. Install it (e.g. brew install ffmpeg) and restart Pitwall.";
            }
            catch (Exception ex)
            {
                // Skip this message but keep going with the others
                logger.LogError(ex, "Failed to transcribe team radio {Key}", key);
                _failed.Add(key);
            }
            finally
            {
                Current = null;
            }
        }
    }
}
