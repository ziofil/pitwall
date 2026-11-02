using Pitwall.Data;
using Spectre.Console;

namespace Pitwall.Console;

/// <summary>
/// Colours and styles shared by the pit wall pages. They follow the conventions of the official
/// timing monitors: purple for the overall fastest, green for a personal best, yellow for a time
/// that didn't improve, on a black background.
/// </summary>
public static class PitWallTheme
{
    public static readonly Color Background = new(0, 0, 0);
    public static readonly Color Panel = new(18, 18, 22);
    public static readonly Color Text = new(235, 235, 235);
    public static readonly Color Muted = new(130, 130, 140);
    public static readonly Color Grid = new(60, 60, 70);

    public static readonly Color Purple = new(178, 0, 255);
    public static readonly Color Green = new(0, 220, 0);
    public static readonly Color Yellow = new(255, 230, 0);
    public static readonly Color Red = new(230, 0, 0);
    public static readonly Color Cyan = new(0, 200, 230);
    public static readonly Color Orange = new(255, 140, 0);

    public static readonly Style Normal = new(foreground: Text);
    public static readonly Style Dim = new(foreground: Muted);
    public static readonly Style Header = new(foreground: Muted, decoration: Decoration.Bold);

    public static Color TeamColour(string? hex) =>
        string.IsNullOrWhiteSpace(hex) ? Text : Color.FromHex(hex.TrimStart('#'));

    /// <summary>Style for a lap or sector time following the timing monitor colour conventions.</summary>
    public static Style TimeStyle(
        TimingDataPoint.Driver.LapSectorTime? time,
        bool improvedOnly = false
    ) =>
        time switch
        {
            null => Dim,
            _ when string.IsNullOrWhiteSpace(time.Value) => Dim,
            { OverallFastest: true } => new Style(foreground: Purple, decoration: Decoration.Bold),
            { PersonalFastest: true } => new Style(foreground: Green, decoration: Decoration.Bold),
            _ => improvedOnly ? Normal : new Style(foreground: Yellow),
        };

    /// <summary>A single-letter compound marker in the compound's colour.</summary>
    public static string CompoundMarkup(string? compound) =>
        compound switch
        {
            "SOFT" => "[bold #ff2a2a]S[/]",
            "MEDIUM" => "[bold #ffd400]M[/]",
            "HARD" => "[bold #f0f0f0]H[/]",
            "INTERMEDIATE" => "[bold #2fbf2f]I[/]",
            "WET" => "[bold #2f7fff]W[/]",
            _ => "[grey]?[/]",
        };

    /// <summary>Formats a number of seconds as a signed gap, e.g. <c>+1.234</c>.</summary>
    public static string Gap(double? seconds, int decimals = 3) =>
        seconds is null
            ? ""
            : seconds.Value.ToString(
                (seconds.Value >= 0 ? "+" : "") + "0." + new string('0', decimals)
            );

    /// <summary>Formats a number of seconds as a lap time, e.g. <c>1:31.234</c>.</summary>
    public static string LapTime(double? seconds) =>
        seconds is null
            ? ""
            : TimeSpan
                .FromSeconds(seconds.Value)
                .ToString(seconds.Value >= 60 ? @"m\:ss\.fff" : @"ss\.fff");
}
