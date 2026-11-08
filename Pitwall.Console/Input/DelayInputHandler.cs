using System.Globalization;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// D opens a box to type the stream delay into, so the pit wall can be matched to a TV
/// broadcast exactly.
/// </summary>
public class DelayInputHandler(IDateTimeProvider dateTimeProvider, State state) : IInputHandler
{
    public const string Title = "LIVE DELAY";

    public bool IsEnabled => !dateTimeProvider.IsPaused;

    public Screen[] ApplicableScreens =>
        [
            Screen.PitWallTiming,
            Screen.PitWallStrategy,
            Screen.PitWallTelemetry,
            Screen.PitWallTrackMap,
            Screen.PitWallComms,
            Screen.PitWallWeather,
        ];

    public ConsoleKey[] Keys => [ConsoleKey.D];

    public string Description => $"Delay {State.FormatDelay(dateTimeProvider.Delay)}";

    public int Sort => 22;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        state.TextInput = new TextInput(
            $"{(long)dateTimeProvider.Delay.TotalMilliseconds}",
            text =>
            {
                if (Parse(text) is not { } delay)
                    return Task.FromResult<string?>(
                        "Use ms (34600), seconds (34.6s) or m:ss (0:34.6)"
                    );
                dateTimeProvider.Delay = delay;
                state.DelayChangedAt = DateTimeOffset.UtcNow;
                return Task.FromResult<string?>(null);
            },
            Title
        );
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads a delay: milliseconds by default (<c>34600</c>), or seconds (<c>34.6s</c>), or
    /// minutes and seconds (<c>0:34.6</c>). <c>null</c> if it isn't one of those or is negative.
    /// </summary>
    public static TimeSpan? Parse(string text)
    {
        text = text.Replace(" ", "").ToLowerInvariant();
        TimeSpan? delay =
            text.EndsWith("ms") && Number(text[..^2]) is { } ms ? TimeSpan.FromMilliseconds(ms)
            : text.EndsWith('s') && Number(text[..^1]) is { } s ? TimeSpan.FromSeconds(s)
            : text.Contains(':')
            && TimeSpan.TryParseExact(
                text,
                [@"m\:ss", @"m\:ss\.FFF", @"h\:mm\:ss", @"h\:mm\:ss\.FFF"],
                CultureInfo.InvariantCulture,
                out var time
            )
                ? time
            : Number(text) is { } plain ? TimeSpan.FromMilliseconds(plain)
            : null;
        return delay < TimeSpan.Zero ? null : delay;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            ? x
            : null;
}
