using System.IO.Compression;
using System.Reflection;

namespace Pitwall.Console;

/// <summary>
/// A session built into the app, so it can be tried without anything downloaded or recorded,
/// including from the single-file executable. Each is a complete session imported from F1's
/// archive, kept gzipped and unpacked to a temporary folder the first time it's played.
/// </summary>
/// <param name="Name">What the menu calls it.</param>
/// <param name="Description">The menu's line about it.</param>
/// <param name="Folder">Its folder under <c>Sample Data</c>, and its resource names.</param>
/// <param name="StartAt">Where in the session it starts playing.</param>
public sealed record DemoSession(
    string Name,
    string Description,
    string Folder,
    DateTimeOffset StartAt
)
{
    /// <summary>
    /// The 2026 United States Grand Prix, from just before the lights go out (20:03:49 UTC) to
    /// the chequered flag, with two virtual safety cars.
    /// </summary>
    public static readonly DemoSession Race = new(
        "Demo: 2026 United States Grand Prix",
        "The whole race in Austin, lights out to the flag, with pit stops and two virtual safety cars. Built in, nothing to download.",
        "2026_Austin_Race",
        new DateTimeOffset(2026, 10, 25, 20, 3, 0, TimeSpan.Zero)
    );

    /// <summary>
    /// 2026 United States Grand Prix qualifying, from a minute before Q1 (21:00 UTC) to the end
    /// of Q3, with no red flags, so it flows without waiting.
    /// </summary>
    public static readonly DemoSession Qualifying = new(
        "Demo: 2026 United States GP qualifying",
        "Q1 to Q3 in Austin, with every car's telemetry. Built in, nothing to download.",
        "2026_Austin_Qualifying",
        new DateTimeOffset(2026, 10, 24, 20, 59, 0, TimeSpan.Zero)
    );

    public static IReadOnlyList<DemoSession> All => [Race, Qualifying];

    /// <summary>
    /// Unpacks the recording to a temporary folder, if it isn't there already, and returns that
    /// folder, ready to replay.
    /// </summary>
    public string Unpack()
    {
        var directory = Path.Join(Path.GetTempPath(), "pitwall-demo", Folder);
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly();

        Stream Resource(string file) =>
            assembly.GetManifestResourceStream($"Demo.{Folder}.{file}")
            ?? throw new InvalidOperationException($"The {Name} recording is missing {file}");

        // A marker records which build of the recording is unpacked (by its compressed size),
        // so a newer one replaces it; files are written under a temporary name first, so an
        // interrupted unpack is never mistaken for a complete one
        using var compressed = Resource("live.jsonl.gz");
        var marker = Path.Join(directory, ".unpacked");
        var version = $"{compressed.Length}";
        if (File.Exists(marker) && File.ReadAllText(marker) == version)
            return directory;

        WriteAtomically(Path.Join(directory, "subscribe.json"), Resource("subscribe.json"));
        WriteAtomically(
            Path.Join(directory, "live.jsonl"),
            new GZipStream(compressed, CompressionMode.Decompress)
        );
        File.WriteAllText(marker, version);
        return directory;
    }

    private static void WriteAtomically(string path, Stream source)
    {
        var partial = path + ".partial";
        using (source)
        using (var output = File.Create(partial))
            source.CopyTo(output);
        File.Move(partial, path, overwrite: true);
    }
}
