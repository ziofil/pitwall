using Pitwall.Console.Graphics;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// Options specific to the console app part of Pitwall.
/// </summary>
public sealed record ConsoleOptions : LiveTimingOptions
{
    /// <summary>
    /// Try to conform to Windows/XDG directory standards by default.
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> will return <c>%APPDATA%</c> on Windows,
    /// <c>$XDG_CONFIG_HOME</c> or <c>~/.config</c> on Linux/Mac.
    /// </summary>
    ///
    /// <returns>
    /// <c>%APPDATA%/pitwall/config.json</c> on Windows,
    /// <c>$XDG_CONFIG_HOME/pitwall/config.json</c> or <c>~/.config/pitwall/config.json</c> on Mac/Linux.
    /// </returns>
    public static string ConfigFilePath => GetConfigFilePath();

    /// <summary>
    /// Prefer to use FFmpeg (<c>ffplay</c>) for audio playback (e.g. Team Radio) instead of more native options
    /// such as <c>mpg123</c> or <c>afplay</c>. FFmpeg is always used on Windows.
    /// Defaults to <see langword="false"/> .
    /// </summary>
    public bool PreferFfmpegPlayback { get; set; } = false;

    /// <summary>
    /// If provided, forces the app to output images using the given protocol.
    /// Otherwise, heuristics and queries will be used to determine if graphics are supported, and which protocol to use.
    /// </summary>
    public GraphicsProtocol? ForceGraphicsProtocol { get; set; } = null;

    /// <summary>
    /// Whwther the app should try to prevent the display from turning off/sleeping whilst running.
    /// This is similar to running <c>caffeinate -d pitwall</c> on macOS.
    /// </summary>
    public bool PreventDisplaySleep { get; set; } = false;

    /// <summary>
    /// Whether car positions on the driver tracker glide between updates by fitting a line to
    /// recent samples, rather than jumping to each raw sample as it arrives. The smoothed dot
    /// lags live slightly so it can be fitted from the samples around it.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool EnablePositionSmoothing { get; set; } = false;

    /// <inheritdoc cref="ExternalPlayerSync.SyncOptions" />
    public ExternalPlayerSync.SyncOptions? ExternalPlayerSync { get; set; }

    private static string GetConfigFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Join(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create
                ),
                "pitwall",
                "config.json"
            );
        }
        else
        {
            var xdgConfigDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(xdgConfigDirectory))
            {
                xdgConfigDirectory = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            return Path.Join(xdgConfigDirectory, "pitwall", "config.json");
        }
    }
}
