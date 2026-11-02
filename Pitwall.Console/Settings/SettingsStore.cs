using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;
using Pitwall.Console.Graphics;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>How a setting is changed on the settings screen.</summary>
public enum SettingKind
{
    /// <summary>On or off; Enter switches it.</summary>
    Toggle,

    /// <summary>One of a few options; Enter moves to the next.</summary>
    Choice,

    /// <summary>A folder path, typed in.</summary>
    Folder,

    /// <summary>Other text, typed in.</summary>
    Text,
}

/// <summary>A setting on the settings screen.</summary>
/// <param name="Path">Its key in config.json, with <c>:</c> for nested settings.</param>
/// <param name="AppliesNow">Whether a change takes effect straight away, or after a restart.</param>
public sealed record Setting(
    string Path,
    string Label,
    string Help,
    SettingKind Kind,
    bool AppliesNow,
    string[]? Choices = null
);

/// <summary>
/// The settings that can be changed from the settings screen: their current values, whether
/// something other than config.json is overriding them, and saving changes to config.json (and
/// to the running app, for the settings that can change while it runs).
/// </summary>
public sealed class SettingsStore(
    IOptions<ConsoleOptions> consoleOptions,
    IOptions<LiveTimingOptions> liveTimingOptions,
    IConfiguration? configuration = null
)
{
    private const string Automatic = "Automatic";

    // Values saved this run that only take effect after a restart, as shown on screen
    private readonly Dictionary<string, string> _pending = [];

    /// <summary>A value saved this run that takes effect after a restart, if there is one.</summary>
    public string? Pending(Setting setting) => _pending.GetValueOrDefault(setting.Path);

    public IReadOnlyList<Setting> Settings { get; } =
    [
        new(
            nameof(LiveTimingOptions.DataDirectory),
            "Data folder",
            "Where sessions are recorded to and replayed from, and the transcription model is kept.",
            SettingKind.Folder,
            AppliesNow: false
        ),
        new(
            nameof(LiveTimingOptions.LogDirectory),
            "Log folder",
            "Where log files are written.",
            SettingKind.Folder,
            AppliesNow: false
        ),
        new(
            nameof(LiveTimingOptions.Notify),
            "Race control bell",
            "Ring the terminal bell when race control sends a new message.",
            SettingKind.Toggle,
            AppliesNow: true
        ),
        new(
            nameof(ConsoleOptions.EnablePositionSmoothing),
            "Smooth car positions",
            "Cars glide between position updates on the track page, slightly behind live.",
            SettingKind.Toggle,
            AppliesNow: true
        ),
        new(
            nameof(ConsoleOptions.PreferFfmpegPlayback),
            "Play radio with FFmpeg",
            "Use ffplay for team radio instead of afplay (macOS) or mpg123 (Linux).",
            SettingKind.Toggle,
            AppliesNow: true
        ),
        new(
            nameof(ConsoleOptions.PreventDisplaySleep),
            "Keep the display awake",
            "Stop the screen sleeping while Pitwall runs, so the stream doesn't disconnect.",
            SettingKind.Toggle,
            AppliesNow: false
        ),
        new(
            nameof(ConsoleOptions.ForceGraphicsProtocol),
            "Image protocol",
            "How images are drawn in the terminal. Automatic detects what the terminal supports.",
            SettingKind.Choice,
            AppliesNow: false,
            [Automatic, .. Enum.GetNames<GraphicsProtocol>()]
        ),
        new(
            nameof(LiveTimingOptions.Verbose),
            "Detailed logs",
            "Write more detailed logs, for troubleshooting.",
            SettingKind.Toggle,
            AppliesNow: false
        ),
    ];

    /// <summary>The setting's value as the running app is using it.</summary>
    public string Value(Setting setting)
    {
        var options = consoleOptions.Value;
        return setting.Path switch
        {
            nameof(LiveTimingOptions.DataDirectory) => options.DataDirectory,
            nameof(LiveTimingOptions.LogDirectory) => options.LogDirectory,
            nameof(LiveTimingOptions.Notify) => OnOff(liveTimingOptions.Value.Notify),
            nameof(ConsoleOptions.EnablePositionSmoothing) => OnOff(
                options.EnablePositionSmoothing
            ),
            nameof(ConsoleOptions.PreferFfmpegPlayback) => OnOff(options.PreferFfmpegPlayback),
            nameof(ConsoleOptions.PreventDisplaySleep) => OnOff(options.PreventDisplaySleep),
            nameof(ConsoleOptions.ForceGraphicsProtocol) =>
                options.ForceGraphicsProtocol?.ToString() ?? Automatic,
            nameof(LiveTimingOptions.Verbose) => OnOff(options.Verbose),
            _ => "",
        };
    }

    /// <summary>
    /// Where the setting is overridden from, if it's also set on the command line or in an
    /// environment variable (which win over config.json), else <c>null</c>.
    /// </summary>
    public string? OverriddenBy(Setting setting)
    {
        if (configuration is not IConfigurationRoot root)
            return null;
        var provider = root.Providers.Reverse().FirstOrDefault(x => x.TryGet(setting.Path, out _));
        return provider switch
        {
            null or JsonConfigurationProvider => null,
            EnvironmentVariablesConfigurationProvider =>
                $"the PITWALL_{setting.Path.Replace(":", "__").ToUpperInvariant()} environment variable",
            _ => "a command line option",
        };
    }

    /// <summary>
    /// The new value when Enter is pressed on a toggle or choice: the opposite, or the next one.
    /// </summary>
    public string Next(Setting setting)
    {
        var current = Pending(setting) ?? Value(setting);
        return setting.Kind switch
        {
            SettingKind.Toggle => current == "On" ? "Off" : "On",
            SettingKind.Choice => setting.Choices![
                (Array.IndexOf(setting.Choices, current) + 1) % setting.Choices.Length
            ],
            _ => current,
        };
    }

    /// <summary>
    /// Checks and saves a new value to config.json, and to the running app if it applies now.
    /// Returns why the value was refused, or <c>null</c> once saved.
    /// </summary>
    public async Task<string?> SaveAsync(Setting setting, string value)
    {
        JsonNode? node;
        switch (setting.Kind)
        {
            case SettingKind.Toggle:
                node = value == "On";
                break;
            case SettingKind.Choice:
                node = value == Automatic ? null : value;
                break;
            case SettingKind.Folder:
                if (string.IsNullOrWhiteSpace(value))
                    return "Enter a folder";
                value = ExpandHome(value);
                try
                {
                    Directory.CreateDirectory(value);
                }
                catch (Exception ex)
                {
                    return $"Can't use that folder: {ex.Message}";
                }
                node = value;
                break;
            default:
                node = string.IsNullOrEmpty(value) ? null : value;
                break;
        }

        await ConfigFile.SetAsync(setting.Path, node);
        if (setting.AppliesNow)
        {
            ApplyNow(setting, value == "On");
        }
        else if (value == Value(setting))
        {
            // Back to what's running: nothing waiting for a restart any more
            _pending.Remove(setting.Path);
        }
        else
        {
            _pending[setting.Path] = value;
        }
        return null;
    }

    private void ApplyNow(Setting setting, bool on)
    {
        switch (setting.Path)
        {
            case nameof(LiveTimingOptions.Notify):
                liveTimingOptions.Value.Notify = on;
                consoleOptions.Value.Notify = on;
                break;
            case nameof(ConsoleOptions.EnablePositionSmoothing):
                consoleOptions.Value.EnablePositionSmoothing = on;
                break;
            case nameof(ConsoleOptions.PreferFfmpegPlayback):
                consoleOptions.Value.PreferFfmpegPlayback = on;
                break;
        }
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/")
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..])
            : path;
}
