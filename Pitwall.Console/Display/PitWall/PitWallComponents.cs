using Spectre.Console;
using Spectre.Console.Rendering;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// Pieces shared by every pit wall page: the header bar with the team, session state, weather
/// and page tabs.
/// </summary>
public sealed class PitWallComponents(
    PitWallContext context,
    SessionInfoProcessor sessionInfo,
    LapCountProcessor lapCount,
    TrackStatusProcessor trackStatus,
    ExtrapolatedClockProcessor extrapolatedClock,
    WeatherProcessor weather,
    TimingDataProcessor timingData,
    IDateTimeProvider dateTimeProvider
)
{
    /// <summary>The pit wall pages, in the order they are cycled through.</summary>
    public static readonly (Screen Screen, string Name)[] Pages =
    [
        (Screen.PitWallTiming, "TIMING"),
        (Screen.PitWallStrategy, "STRATEGY"),
        (Screen.PitWallTelemetry, "TELEMETRY"),
        (Screen.PitWallTrackMap, "TRACK"),
        (Screen.PitWallComms, "COMMS"),
    ];

    public const int HeaderHeight = 2;

    /// <summary>Wraps page content with the header bar.</summary>
    public IRenderable Page(Screen screen, IRenderable content) =>
        new Layout("PitWall").SplitRows(
            new Layout("Header", Header(screen)).Size(HeaderHeight),
            new Layout("Body", content)
        );

    public IRenderable Header(Screen screen)
    {
        var team = context.Team;
        var teamColour = PitWallTheme.TeamColour(team?.Colour);
        var teamHex = teamColour.ToHex();

        // First line: team and its cars, session, lap, track status, clocks, weather
        var cars = team is null
            ? ""
            : string.Join(
                "  ",
                team.DriverNumbers.Select(number =>
                {
                    var line = timingData.Latest.Lines.GetValueOrDefault(number);
                    var tla = context.Driver(number).Tla ?? number;
                    var position = line?.Retired == true ? "OUT" : $"P{line?.Line}";
                    return $"[bold]{tla}[/] [#{teamHex}]{position}[/]";
                })
            );

        var lap = lapCount.Latest;
        // The lap count is only meaningful in races
        var lapText = lap?.CurrentLap is null || !sessionInfo.Latest.IsRace()
            ? ""
            : $"LAP [bold]{lap.CurrentLap}[/]/{lap.TotalLaps}";

        var localOffset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var localTime = dateTimeProvider.Utc.ToOffset(localOffset);

        var w = weather.Latest;
        var rain = w.Rainfall is "1" ? " [bold #2f7fff]RAIN[/]" : "";
        var weatherText = w.AirTemp is null
            ? ""
            : $"AIR {w.AirTemp}° TRK {w.TrackTemp}° HUM {w.Humidity}%{rain}";

        var session = $"{sessionInfo.Latest.Meeting?.Circuit?.ShortName} {sessionInfo.Latest.Name}".Trim();

        var top = new[]
        {
            $"[#{teamHex} on #{teamHex}]▌[/][bold #{teamHex}] {Markup.Escape(team?.Name.ToUpperInvariant() ?? "NO TEAM")} [/]",
            cars,
            Markup.Escape(session.ToUpperInvariant()),
            lapText,
            TrackStatusMarkup(),
            $@"{localTime:HH\:mm\:ss}",
            $@"REM {extrapolatedClock.ExtrapolatedRemaining():hh\:mm\:ss}",
            weatherText,
        };

        // Second line: page tabs and the team colour rule
        var tabs = string.Join(
            " ",
            Pages.Select(
                (page, i) =>
                    page.Screen == screen
                        ? $"[black on #{teamHex}] {i + 1} {page.Name} [/]"
                        : $"[grey] {i + 1} {page.Name} [/]"
            )
        );

        return new Rows(
            new SingleLine(string.Join("[grey] │ [/]", top.Where(x => !string.IsNullOrWhiteSpace(x)))),
            new SingleLine(tabs)
        );
    }

    private string TrackStatusMarkup()
    {
        var status = trackStatus.Latest;
        if (status?.Status is null)
            return "";
        var style = status.Status switch
        {
            "1" => "black on #00dc00",
            "2" or "4" or "6" or "7" => "black on #ffe600",
            "5" => "white on #e60000",
            _ => "white",
        };
        var text = status.Status switch
        {
            "1" => "GREEN",
            "2" => "YELLOW",
            "4" => "SAFETY CAR",
            "5" => "RED FLAG",
            "6" => "VSC",
            "7" => "VSC ENDING",
            _ => status.Message?.ToUpperInvariant() ?? "",
        };
        return $"[{style}] {Markup.Escape(text)} [/]";
    }
}
