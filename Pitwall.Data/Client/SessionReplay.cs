using Microsoft.Extensions.DependencyInjection;

namespace Pitwall.Data;

/// <summary>
/// Replays a recorded session into the processors as fast as possible, instead of in real time
/// like <see cref="IJsonTimingClient"/>. Used to render pages from recordings, e.g. for tests and
/// screenshots.
/// </summary>
public static class SessionReplay
{
    /// <summary>
    /// Processes a recording's subscription data and messages, stopping when
    /// <paramref name="stop"/> returns true. The session clock is kept at each message's time, and
    /// left at the time of the last message processed.
    /// </summary>
    /// <param name="directory">A recording folder holding subscribe.json and live.jsonl (or the older .txt names).</param>
    public static void Replay(IServiceProvider services, string directory, Func<bool> stop)
    {
        var timingService = (TimingService)services.GetRequiredService<ITimingService>();
        var clock = services.GetRequiredService<IDateTimeProvider>();

        timingService.ProcessSubscriptionData(
            File.ReadAllText(FirstExisting(directory, "subscribe.json", "subscribe.txt"))
        );

        var last = DateTimeOffset.UtcNow;
        foreach (var line in File.ReadLines(FirstExisting(directory, "live.jsonl", "live.txt")))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var (type, data, timestamp) = JsonTimingClient.ProcessLine(line);
            // Keep the session clock at the message's time, as it is when the app replays it
            clock.Delay = DateTimeOffset.UtcNow - timestamp;
            timingService.ProcessData(type, data, timestamp);
            last = timestamp;
            if (stop())
                break;
        }

        clock.Delay = DateTimeOffset.UtcNow - last;
    }

    private static string FirstExisting(string directory, params string[] names) =>
        names.Select(x => Path.Join(directory, x)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"None of {string.Join(", ", names)} in {directory}");
}
