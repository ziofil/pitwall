using Spectre.Console;
using Spectre.Console.Rendering;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// A copy of the official timing monitor that every pit wall has: black background, cyan
/// headers and positions, white driver names, sector times coloured purple/green/yellow, the
/// session-best sectors above the sector columns, and the clocks in yellow along the bottom.
/// The selected team's cars are marked in the team colour.
/// </summary>
public sealed class PitWallTimingDisplay(
    PitWallComponents components,
    PitWallContext context,
    TimingDataProcessor timingData,
    SessionInfoProcessor sessionInfo,
    ExtrapolatedClockProcessor extrapolatedClock,
    TimingSmoother smoother,
    IDateTimeProvider dateTimeProvider
) : IDisplay
{
    private const string Cyan = "#33ccff";
    private const string Purple = "#cc33ff";
    private const string Yellow = "#ffff33";

    public Screen Screen => Screen.PitWallTiming;

    // Smoothed values glide between feed updates, so redraw quickly to show the motion
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(66);

    public Task<IRenderable> GetContentAsync()
    {
        var isRace = sessionInfo.Latest.IsRace();
        var body = new Layout("Timing").SplitRows(
            new Layout("Table", isRace ? RaceTable() : PracticeTable()),
            new Layout("Footer", Footer()).Size(1)
        );
        return Task.FromResult(components.Page(Screen, body));
    }

    private IRenderable RaceTable()
    {
        var lines = timingData.Latest.GetOrderedLines();
        if (lines.Count == 0)
            return new Text("NO TIMING AVAILABLE", PitWallTheme.Dim);

        var best = SessionBestSectors();
        var table = NewTable(
            ("POS", 4, Justify.Right),
            ("NO", 4, Justify.Right),
            ("DRIVER", 18, Justify.Left),
            ("GAP", 9, Justify.Right),
            ("INT", 8, Justify.Right),
            ("LAST LAP", 10, Justify.Right),
            (best[0], 7, Justify.Right),
            (best[1], 7, Justify.Right),
            (best[2], 7, Justify.Right),
            ("PIT", 5, Justify.Right),
            ("LAPS", 5, Justify.Right)
        );

        foreach (var (number, line) in lines)
        {
            var retired = line.Retired == true || line.Stopped == true;
            // Show times as continuous values interpolated between feed updates; laps and
            // lap counts ("LAP 31", "1 L") stay as the feed sends them
            var gap = line.GapToLeaderSeconds() is > 0 && smoother.GapToLeader(number) is { } smoothGap
                ? $"+{smoothGap:0.000}"
                : line.GapToLeader;
            var interval = line.IntervalToPositionAhead?.IntervalSeconds() is > 0 && smoother.Interval(number) is { } smoothInterval
                ? $"+{smoothInterval:0.000}"
                : line.IntervalToPositionAhead?.Value;
            var intervalStyle =
                line.IntervalToPositionAhead?.Catching == true
                    ? new Style(foreground: PitWallTheme.Green)
                    : PitWallTheme.Normal;

            table.AddRow(
                Position(number, line),
                Number(number, line),
                Name(number, retired),
                new Text(retired ? "STOP" : gap ?? "", retired ? PitWallTheme.Dim : PitWallTheme.Normal),
                new Text(retired ? "" : interval ?? "", intervalStyle),
                new Text(line.LastLapTime?.Value ?? "", PitWallTheme.TimeStyle(line.LastLapTime, improvedOnly: true)),
                Sector(line, "0"),
                Sector(line, "1"),
                Sector(line, "2"),
                Pit(line),
                new Text($"{line.NumberOfLaps}", PitWallTheme.Normal)
            );
        }
        return table;
    }

    private IRenderable PracticeTable()
    {
        var lines = timingData.Latest.GetOrderedLines();
        if (lines.Count == 0)
            return new Text("NO TIMING AVAILABLE", PitWallTheme.Dim);

        var best = SessionBestSectors();
        var leaderBest = lines.Values.FirstOrDefault()?.BestLapTime.ToTimeSpan();
        var table = NewTable(
            ("POS", 4, Justify.Right),
            ("NO", 4, Justify.Right),
            ("DRIVER", 18, Justify.Left),
            ("BEST LAP", 10, Justify.Right),
            ("GAP", 8, Justify.Right),
            (best[0], 7, Justify.Right),
            (best[1], 7, Justify.Right),
            (best[2], 7, Justify.Right),
            ("LAPS", 5, Justify.Right)
        );

        foreach (var (number, line) in lines)
        {
            var knockedOut = line.KnockedOut == true;
            var bestLap = line.BestLapTime.ToTimeSpan();
            var gap = bestLap is null || leaderBest is null || bestLap == leaderBest
                ? ""
                : (bestLap.Value - leaderBest.Value).TotalSeconds.ToString("0.000");

            table.AddRow(
                Position(number, line),
                Number(number, line, knockedOut),
                Name(number, knockedOut),
                new Text(line.BestLapTime?.Value ?? "", PitWallTheme.Normal),
                new Text(gap, PitWallTheme.Normal),
                Sector(line, "0"),
                Sector(line, "1"),
                Sector(line, "2"),
                new Text($"{line.NumberOfLaps}", PitWallTheme.Normal)
            );
        }
        return table;
    }

    private static Table NewTable(params (string Header, int Width, Justify Alignment)[] columns)
    {
        var table = new Table().NoBorder().NoSafeBorder();
        table.ShowHeaders = true;
        foreach (var (header, width, alignment) in columns)
        {
            // Sector headers show the session-best sector time in purple, like the real monitor
            var markup = header.Contains('.')
                ? $"[bold {Purple}]{header}[/]"
                : $"[bold {Cyan}]{header}[/]";
            table.AddColumn(
                new TableColumn(new Markup(markup))
                {
                    Width = width,
                    Alignment = alignment,
                    NoWrap = true,
                    Padding = new Padding(0, 0, 1, 0),
                }
            );
        }
        return table;
    }

    private IRenderable Position(string number, TimingDataPoint.Driver line)
    {
        // The team's cars get a team-coloured marker in front of the position
        var marker = context.IsTeamCar(number)
            ? $"[{PitWallTheme.TeamColour(context.Team?.Colour).ToMarkup()}]▌[/]"
            : " ";
        return new Markup($"{marker}[bold {Cyan}]{line.Line,2}[/]");
    }

    private IRenderable Number(string number, TimingDataPoint.Driver line, bool knockedOut = false)
    {
        var colour = knockedOut || line.Retired == true ? "#ff3366" : Cyan;
        return new Markup($"[bold {colour}]{number}[/]");
    }

    private IRenderable Name(string number, bool dimmed)
    {
        var driver = context.Driver(number);
        var name = Markup.Escape(FormatName(driver));
        var style = dimmed ? "#8899bb"
            : context.IsTeamCar(number) ? $"bold {PitWallTheme.TeamColour(context.Team?.Colour).ToMarkup()}"
            : "bold white";
        return new Markup($"[{style}]{name}[/]");
    }

    /// <summary>Formats a name as on the timing monitor, e.g. <c>L. NORRIS</c>.</summary>
    private static string FormatName(DriverListDataPoint.Driver driver)
    {
        var broadcast = driver.BroadcastName ?? driver.Tla ?? "";
        var parts = broadcast.Split(' ', 2);
        return parts.Length == 2 && parts[0].Length == 1
            ? $"{parts[0]}. {parts[1]}".ToUpperInvariant()
            : broadcast.ToUpperInvariant();
    }

    private static IRenderable Sector(TimingDataPoint.Driver line, string sector)
    {
        var time = line.Sectors.GetValueOrDefault(sector);
        return new Text(ShortSector(time?.Value), PitWallTheme.TimeStyle(time));
    }

    // The monitor shows sector times to one decimal place
    private static string ShortSector(string? value) =>
        decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (Math.Floor(seconds * 10) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : value ?? "";

    private static IRenderable Pit(TimingDataPoint.Driver line) =>
        line switch
        {
            { Retired: true } or { Stopped: true } => new Text(""),
            { InPit: true } => new Text("PIT", new Style(foreground: PitWallTheme.Red, decoration: Decoration.Bold)),
            { PitOut: true } => new Text("OUT", new Style(foreground: PitWallTheme.Green, decoration: Decoration.Bold)),
            _ => new Text($"{line.NumberOfPitStops}", PitWallTheme.Normal),
        };

    /// <summary>The fastest time set in each sector so far this session, to one decimal place.</summary>
    private string[] SessionBestSectors()
    {
        var all = timingData
            .DriversByLap.Values.SelectMany(x => x.Values)
            .Concat(timingData.Latest.Lines.Values)
            .ToList();
        return
        [
            .. Enumerable
                .Range(0, 3)
                .Select(i =>
                {
                    var best = all.Select(x =>
                            decimal.TryParse(
                                x.Sectors.GetValueOrDefault(i.ToString())?.Value,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var s
                            )
                                ? s
                                : (decimal?)null
                        )
                        .Where(x => x > 0)
                        .Min();
                    return best is null ? $"S{i + 1}" : ShortSector(best.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }),
        ];
    }

    private IRenderable Footer()
    {
        var localOffset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var localTime = dateTimeProvider.Utc.ToOffset(localOffset);
        var remaining = extrapolatedClock.ExtrapolatedRemaining();
        var session = Markup.Escape(sessionInfo.Latest.Name?.ToUpperInvariant() ?? "");

        return new Columns(
            new Markup($@"[bold {Yellow}]{localTime:HH\:mm\:ss}[/]"),
            new Markup($"[grey]{session}[/]").Centered(),
            new Markup($@"[bold {Yellow}]{remaining:h\:mm\:ss}[/]").RightJustified()
        )
        {
            Expand = true,
        };
    }
}
