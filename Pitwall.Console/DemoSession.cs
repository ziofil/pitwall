using System.Reflection;

namespace Pitwall.Console;

/// <summary>
/// The demo: the 2026 Mexico City Grand Prix, from lap 28 to the finish, with a safety car and a
/// red flag. The recording is built into the app, so the demo works without any data downloaded
/// or recorded, including from the single-file executable.
/// </summary>
public static class DemoSession
{
    public const string Name = "2026 Mexico City Grand Prix";

    private const string Folder = "2026_Mexico_City_Race";

    /// <summary>
    /// Where the demo starts: the recording's timing data begins at 20:42:11, but car telemetry
    /// and positions only from 20:43:28, so start just after that rather than with empty pages.
    /// </summary>
    public static readonly DateTimeOffset StartAt = new(2026, 11, 1, 20, 43, 30, TimeSpan.Zero);
    private static readonly string[] Files = ["subscribe.json", "live.jsonl"];

    /// <summary>
    /// Unpacks the built-in recording to a temporary folder, if it isn't there already, and
    /// returns that folder, ready to replay.
    /// </summary>
    public static string Unpack()
    {
        var directory = Path.Join(Path.GetTempPath(), "pitwall-demo", Folder);
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var file in Files)
        {
            using var resource =
                assembly.GetManifestResourceStream($"Demo.{file}")
                ?? throw new InvalidOperationException($"The demo recording is missing {file}");
            var path = Path.Join(directory, file);
            if (File.Exists(path) && new FileInfo(path).Length == resource.Length)
                continue;

            // Write to a temporary name first, so an interrupted unpack is never mistaken for a
            // complete one
            var partial = path + ".partial";
            using (var output = File.Create(partial))
                resource.CopyTo(output);
            File.Move(partial, path, overwrite: true);
        }

        return directory;
    }
}
