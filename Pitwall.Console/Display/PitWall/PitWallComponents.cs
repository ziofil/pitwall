using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// Pieces shared by every pit wall page: the header bar with the team, session state, weather
/// and page tabs.
/// </summary>
public sealed class PitWallComponents(
    State state,
    RadioTranscriber transcriber,
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
        (Screen.PitWallWeather, "WEATHER"),
    ];

    public const int HeaderHeight = 2;

    /// <summary>
    /// Rows of the window that aren't page body: the header above and the key hints line below.
    /// </summary>
    public const int BodyRowsOffset = HeaderHeight + 1;

    /// <summary>Wraps page content with the header bar.</summary>
    public IRenderable Page(Screen screen, IRenderable content)
    {
        // Keep the team's radio transcribed in the background whichever page is showing
        transcriber.Tick();
        return new Layout("PitWall").SplitRows(
            new Layout("Header", Header(screen)).Size(HeaderHeight),
            new Layout("Body", content)
        );
    }

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
        var lapText =
            lap?.CurrentLap is null || !sessionInfo.Latest.IsRace()
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

        var session =
            $"{sessionInfo.Latest.Meeting?.Circuit?.ShortName} {sessionInfo.Latest.Name}".Trim();

        // Each item with the order it's dropped in when the line doesn't fit (0: never)
        var top = new (string Markup, int Drop)[]
        {
            (
                $"[#{teamHex} on #{teamHex}]▌[/][bold #{teamHex}] {Markup.Escape(team?.Name.ToUpperInvariant() ?? "NO TEAM")} [/]",
                0
            ),
            (cars, 0),
            (Markup.Escape(session.ToUpperInvariant()), 3),
            (lapText, 0),
            (TrackStatusMarkup(), 0),
            ($@"{localTime:HH\:mm\:ss}", 2),
            ($@"REM {extrapolatedClock.ExtrapolatedRemaining():h\:mm\:ss}", 4),
            (DelayMarkup(), 0),
            (weatherText, 1),
        }
            .Where(x => !string.IsNullOrWhiteSpace(x.Markup))
            .ToList();

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

        // While the delay is being typed, its box takes the place of the tabs
        if (state.TextInput is { Title: DelayInputHandler.Title } input)
        {
            var error = input.Error is null
                ? "[grey]ms, or 34.6s, or 0:34.6 · 0 for none[/]"
                : $"[#ff3333]{Markup.Escape(input.Error)}[/]";
            tabs =
                $"[bold #{teamHex}] {DelayInputHandler.Title} [/][black on white] {Markup.Escape(input.Text)}▏ [/] [grey]ms[/]   {error}";
        }

        return new Rows(
            new Responsive(width =>
            {
                const string separator = "[grey] │ [/]";
                var shown = top.ToList();
                int Length() =>
                    shown.Sum(x => Markup.Remove(x.Markup).Length) + (3 * (shown.Count - 1));
                foreach (var drop in top.Where(x => x.Drop > 0).OrderBy(x => x.Drop))
                {
                    if (Length() <= width)
                        break;
                    shown.Remove(drop);
                }
                return new SingleLine(string.Join(separator, shown.Select(x => x.Markup)));
            }),
            new SingleLine(tabs)
        );
    }

    /// <summary>
    /// The stream delay: highlighted for a few seconds after it changes so it can be watched
    /// while adjusting it, then grey. Hidden when there's no delay, and shows when paused.
    /// </summary>
    private string DelayMarkup()
    {
        var delay = State.FormatDelay(dateTimeProvider.Delay);
        var paused = dateTimeProvider.IsPaused ? " [bold white on #c80000] PAUSED [/]" : "";
        var justChanged = DateTimeOffset.UtcNow - state.DelayChangedAt < TimeSpan.FromSeconds(3);

        return justChanged ? $"[bold black on #ffe600] DELAY {delay} [/]{paused}"
            : dateTimeProvider.Delay > TimeSpan.Zero ? $"[grey]DELAY {delay}[/]{paused}"
            : paused.TrimStart();
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
