using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// A copy of the official timing monitor that every pit wall has: black background, cyan
/// headers and positions, white driver names, sector times coloured purple/green/yellow, the
/// session-best sectors above the sector columns, and the clocks in yellow along the bottom.
/// The selected team's cars are marked in the team colour. On wide terminals the team's cars
/// get a panel each with their recent laps and sectors, and race control's latest messages
/// fill the space under the table.
/// </summary>
public sealed class PitWallTimingDisplay(
    PitWallComponents components,
    PitWallContext context,
    TimingDataProcessor timingData,
    SessionInfoProcessor sessionInfo,
    ExtrapolatedClockProcessor extrapolatedClock,
    TimingSmoother smoother,
    TimingAppDataProcessor timingAppData,
    RaceControlMessageProcessor raceControl,
    IDateTimeProvider dateTimeProvider
) : IDisplay
{
    private const string Cyan = "#33ccff";
    private const string Purple = "#cc33ff";
    private const string Yellow = "#ffff33";

    // Table widths (columns plus their padding) that the best lap and tyre columns need, and
    // that full driver names need; narrower than that, drivers are shown by their three letters
    private const int ExtraColumnsMinWidth = 114;
    private const int FullNamesMinWidth = 96;
    private const int SidePanelWidth = 46;
    // The team panels only come in when the table beside them keeps its extra columns
    private const int SidePanelMinWidth = SidePanelWidth + ExtraColumnsMinWidth;

    public Screen Screen => Screen.PitWallTiming;

    // Smoothed values glide between feed updates, so redraw quickly to show the motion
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(66);

    public Task<IRenderable> GetContentAsync()
    {
        var isRace = sessionInfo.Latest.IsRace();
        // The table's height: one row per car plus the two header lines
        var tableRows = timingData.Latest.Lines.Count + 2;

        var content = new Responsive(width =>
        {
            // Best lap and tyre columns only when the table has room for them, and mini sectors
            // after those
            var tableWidth = width < SidePanelMinWidth ? width : width - SidePanelWidth;
            var shortNames = tableWidth < FullNamesMinWidth;
            var table = isRace
                ? RaceTable(wide: tableWidth >= ExtraColumnsMinWidth, tableWidth, shortNames)
                : PracticeTable(tableWidth, shortNames);
            // Race control fills whatever height the table leaves
            var left = new Layout("Left").SplitRows(
                new Layout("Table", table).Size(tableRows),
                new Layout("Gap", new Text("")).Size(1),
                new Layout("RaceControl", RaceControlPanel())
            );
            return width < SidePanelMinWidth
                ? left
                : new Layout("Wide").SplitColumns(
                    left,
                    new Layout("Team", TeamPanels()).Size(SidePanelWidth)
                );
        });

        var body = new Layout("Timing").SplitRows(
            new Layout("Content", content),
            new Layout("Footer", Footer()).Size(1)
        );
        return Task.FromResult(components.Page(Screen, body));
    }

    private IRenderable RaceTable(bool wide, int available, bool shortNames)
    {
        var lines = timingData.Latest.GetOrderedLines();
        if (lines.Count == 0)
            return new Text("NO TIMING AVAILABLE", PitWallTheme.Dim);

        var best = SessionBestSectors();
        List<(string Header, int Width, Justify Alignment, string Best)> columns =
        [
            ("POS", 4, Justify.Right, ""),
            ("NO", 4, Justify.Right, ""),
            ("DRIVER", shortNames ? 6 : 18, Justify.Left, ""),
            ("GAP", 9, Justify.Right, ""),
            ("INT", 8, Justify.Right, ""),
            ("LAST LAP", 10, Justify.Right, ""),
            .. wide
                ? [("BEST", 10, Justify.Right, "")]
                : Array.Empty<(string, int, Justify, string)>(),
            ("S1", 7, Justify.Right, best[0]),
            ("S2", 7, Justify.Right, best[1]),
            ("S3", 7, Justify.Right, best[2]),
            ("PIT", 5, Justify.Right, ""),
            .. wide
                ? [("TYRE", 6, Justify.Right, "")]
                : Array.Empty<(string, int, Justify, string)>(),
            ("LAPS", 5, Justify.Right, ""),
        ];
        var segments = MiniSectors(columns, available);
        var table = NewTable([.. columns]);

        var fastestLap = lines
            .Values.Select(x => x.BestLapTime.ToTimeSpan())
            .Where(x => x > TimeSpan.Zero)
            .DefaultIfEmpty()
            .Min();

        foreach (var (number, line) in lines)
        {
            var retired = line.Retired == true || line.Stopped == true;
            // Show times as continuous values interpolated between feed updates; laps and
            // lap counts ("LAP 31", "1 L") stay as the feed sends them
            var gap =
                line.GapToLeaderSeconds() is > 0 && smoother.GapToLeader(number) is { } smoothGap
                    ? $"+{smoothGap:0.000}"
                    : line.GapToLeader;
            var interval =
                line.IntervalToPositionAhead?.IntervalSeconds() is > 0
                && smoother.Interval(number) is { } smoothInterval
                    ? $"+{smoothInterval:0.000}"
                    : line.IntervalToPositionAhead?.Value;
            var intervalStyle =
                line.IntervalToPositionAhead?.Catching == true
                    ? new Style(foreground: PitWallTheme.Green)
                    : PitWallTheme.Normal;

            List<IRenderable> cells =
            [
                Position(number, line),
                Number(number, line),
                Name(number, retired, shortNames),
                new Text(
                    retired ? "STOP" : gap ?? "",
                    retired ? PitWallTheme.Dim : PitWallTheme.Normal
                ),
                new Text(retired ? "" : interval ?? "", intervalStyle),
                new Text(
                    line.LastLapTime?.Value ?? "",
                    PitWallTheme.TimeStyle(line.LastLapTime, improvedOnly: true)
                ),
                .. wide ? [BestLap(line, fastestLap)] : Array.Empty<IRenderable>(),
                Sector(line, 0, segments),
                Sector(line, 1, segments),
                Sector(line, 2, segments),
                Pit(line),
                .. wide ? [Tyre(number)] : Array.Empty<IRenderable>(),
                new Text($"{line.NumberOfLaps}", PitWallTheme.Normal),
            ];
            table.AddRow(cells);
        }
        return table;
    }

    private IRenderable PracticeTable(int available, bool shortNames)
    {
        var lines = timingData.Latest.GetOrderedLines();
        if (lines.Count == 0)
            return new Text("NO TIMING AVAILABLE", PitWallTheme.Dim);

        var best = SessionBestSectors();
        var leaderBest = lines.Values.FirstOrDefault()?.BestLapTime.ToTimeSpan();
        List<(string Header, int Width, Justify Alignment, string Best)> columns =
        [
            ("POS", 4, Justify.Right, ""),
            ("NO", 4, Justify.Right, ""),
            ("DRIVER", shortNames ? 6 : 18, Justify.Left, ""),
            ("BEST LAP", 10, Justify.Right, ""),
            ("GAP", 8, Justify.Right, ""),
            ("S1", 7, Justify.Right, best[0]),
            ("S2", 7, Justify.Right, best[1]),
            ("S3", 7, Justify.Right, best[2]),
            ("LAPS", 5, Justify.Right, ""),
        ];
        var segments = MiniSectors(columns, available);
        var table = NewTable([.. columns]);

        foreach (var (number, line) in lines)
        {
            var knockedOut = line.KnockedOut == true;
            var bestLap = line.BestLapTime.ToTimeSpan();
            var gap =
                bestLap is null || leaderBest is null || bestLap == leaderBest
                    ? ""
                    : (bestLap.Value - leaderBest.Value).TotalSeconds.ToString("0.000");

            table.AddRow(
                Position(number, line),
                Number(number, line, knockedOut),
                Name(number, knockedOut, shortNames),
                new Text(line.BestLapTime?.Value ?? "", PitWallTheme.Normal),
                new Text(gap, PitWallTheme.Normal),
                Sector(line, 0, segments),
                Sector(line, 1, segments),
                Sector(line, 2, segments),
                new Text($"{line.NumberOfLaps}", PitWallTheme.Normal)
            );
        }
        return table;
    }

    /// <summary>
    /// A table with two header lines: the column titles in cyan, and beneath the sector titles
    /// the session-best sector times in purple, as the real monitor shows them.
    /// </summary>
    private static Table NewTable(
        params (string Header, int Width, Justify Alignment, string Best)[] columns
    )
    {
        var table = new Table().NoBorder().NoSafeBorder();
        table.ShowHeaders = true;
        foreach (var (header, width, alignment, best) in columns)
        {
            var markup = $"[bold {Cyan}]{header}[/]\n[bold {Purple}]{best}[/]";
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
        return new Markup($"{marker}[bold {Cyan}]{line.Line, 2}[/]");
    }

    private IRenderable Number(string number, TimingDataPoint.Driver line, bool knockedOut = false)
    {
        var colour = knockedOut || line.Retired == true ? "#ff3366" : Cyan;
        return new Markup($"[bold {colour}]{number}[/]");
    }

    private IRenderable Name(string number, bool dimmed, bool shortName)
    {
        var driver = context.Driver(number);
        var name = Markup.Escape(shortName ? driver.Tla ?? number : FormatName(driver));
        var style =
            dimmed ? "#8899bb"
            : context.IsTeamCar(number)
                ? $"bold {PitWallTheme.TeamColour(context.Team?.Colour).ToMarkup()}"
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

    /// <summary>
    /// How many mini sectors each sector has, if the table has room to show them; widens the
    /// sector columns to fit them. <c>null</c> when they aren't shown.
    /// </summary>
    private int[]? MiniSectors(
        List<(string Header, int Width, Justify Alignment, string Best)> columns,
        int available
    )
    {
        // Not every car has reached every mini sector, so count the most any car has
        var counts = Enumerable
            .Range(0, 3)
            .Select(sector =>
                timingData
                    .Latest.Lines.Values.Select(line =>
                        line.Sectors.GetValueOrDefault($"{sector}")
                            ?.Segments.Keys.DefaultIfEmpty(-1)
                            .Max() + 1
                        ?? 0
                    )
                    .DefaultIfEmpty(0)
                    .Max()
            )
            .ToArray();

        // Each column also has a space of padding; the bars take one more for the gap after them
        var width = columns.Sum(x => x.Width + 1) + counts.Sum(x => x + 1);
        if (counts.Sum() == 0 || width > available)
            return null;

        for (var sector = 0; sector < 3; sector++)
        {
            var index = columns.FindIndex(x => x.Header == $"S{sector + 1}");
            columns[index] = columns[index] with
            {
                Width = columns[index].Width + counts[sector] + 1,
            };
        }
        return counts;
    }

    private static IRenderable Sector(TimingDataPoint.Driver line, int sector, int[]? segments)
    {
        var time = line.Sectors.GetValueOrDefault($"{sector}");
        var text = new Text(ShortSector(time?.Value), PitWallTheme.TimeStyle(time));
        if (segments is null)
            return text;

        // One bar per mini sector, coloured as the car went through it, then the sector time
        var bars = string.Concat(
            Enumerable
                .Range(0, segments[sector])
                .Select(i => $"[{SegmentColour(time?.Segments.GetValueOrDefault(i)?.Status)}]█[/]")
        );
        var timeText = ShortSector(time?.Value).PadLeft(7);
        return new Markup($"{bars} [{PitWallTheme.TimeStyle(time).ToMarkup()}]{timeText}[/]");
    }

    private static string SegmentColour(TimingDataPoint.Driver.StatusFlags? status) =>
        status switch
        {
            null or 0 => "#333338",
            { } s when s.HasFlag(TimingDataPoint.Driver.StatusFlags.PitLane) => "#2f7fff",
            { } s when s.HasFlag(TimingDataPoint.Driver.StatusFlags.OverallBest) => Purple,
            { } s when s.HasFlag(TimingDataPoint.Driver.StatusFlags.PersonalBest) => "#00dc00",
            _ => Yellow,
        };

    // The monitor shows sector times to one decimal place
    private static string ShortSector(string? value) =>
        decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (Math.Floor(seconds * 10) / 10).ToString(
                "0.0",
                System.Globalization.CultureInfo.InvariantCulture
            )
            : value ?? "";

    private static IRenderable Pit(TimingDataPoint.Driver line) =>
        line switch
        {
            { Retired: true } or { Stopped: true } => new Text(""),
            { InPit: true } => new Text(
                "PIT",
                new Style(foreground: PitWallTheme.Red, decoration: Decoration.Bold)
            ),
            { PitOut: true } => new Text(
                "OUT",
                new Style(foreground: PitWallTheme.Green, decoration: Decoration.Bold)
            ),
            _ => new Text($"{line.NumberOfPitStops ?? 0}", PitWallTheme.Normal),
        };

    /// <summary>A car's best lap, in purple if it's the fastest lap of the session.</summary>
    private static IRenderable BestLap(TimingDataPoint.Driver line, TimeSpan? fastest)
    {
        var best = line.BestLapTime.ToTimeSpan();
        var style =
            best is not null && best == fastest
                ? new Style(foreground: PitWallTheme.Purple, decoration: Decoration.Bold)
                : PitWallTheme.Dim;
        return new Text(line.BestLapTime?.Value ?? "", style);
    }

    /// <summary>The tyre the car is on and how many laps old it is, e.g. <c>M 12</c>.</summary>
    private IRenderable Tyre(string number)
    {
        var stint = timingAppData
            .Latest.Lines.GetValueOrDefault(number)
            ?.Stints.OrderBy(x => int.TryParse(x.Key, out var i) ? i : 0)
            .LastOrDefault()
            .Value;
        return stint is null
            ? new Text("")
            : new Markup($"{PitWallTheme.CompoundMarkup(stint.Compound)} {stint.TotalLaps, 2}");
    }

    /// <summary>For each of the team's cars, its recent laps with sector times, best sectors and ideal lap.</summary>
    private IRenderable TeamPanels()
    {
        var team = context.Team;
        if (team is null)
            return new Text("");

        // One half of the column each: panels in a plain list would each take the full height
        return new Layout("TeamCars").SplitRows(
            team.DriverNumbers.Select(number => new Layout(number, CarPanel(number, team)))
                .ToArray()
        );
    }

    private Panel CarPanel(string number, PitWallTeam team)
    {
        var driver = context.Driver(number);
        var hex = PitWallTheme.TeamColour(team.Colour).ToHex();
        var line = timingData.Latest.Lines.GetValueOrDefault(number);

        var table = new Table().NoBorder().NoSafeBorder();
        foreach (
            var (header, width) in new[]
            {
                ("LAP", 3),
                ("TIME", 9),
                ("S1", 5),
                ("S2", 5),
                ("S3", 5),
            }
        )
        {
            table.AddColumn(
                new TableColumn(new Markup($"[bold {Cyan}]{header}[/]"))
                {
                    Width = width,
                    Alignment = Justify.Right,
                    NoWrap = true,
                    Padding = new Padding(0, 0, 1, 0),
                }
            );
        }

        // The most recent laps, newest first, as recorded at the end of each lap. Newest first so
        // that a short panel cuts the oldest laps off the bottom.
        var laps = timingData
            .DriversByLap.Where(x => x.Value.ContainsKey(number))
            .OrderByDescending(x => x.Key)
            .Take(14)
            .ToList();
        foreach (var (lap, lines) in laps)
        {
            var snapshot = lines[number];
            var pit = snapshot.IsPitLap ? "[#ff3333]P[/]" : " ";
            table.AddRow(
                new Markup($"[grey]{lap}[/]{pit}"),
                new Text(
                    snapshot.LastLapTime?.Value ?? "",
                    PitWallTheme.TimeStyle(snapshot.LastLapTime, improvedOnly: true)
                ),
                Sector(snapshot, 0, null),
                Sector(snapshot, 1, null),
                Sector(snapshot, 2, null)
            );
        }

        // Best sectors this session and the lap they would add up to
        var bests = Enumerable
            .Range(0, 3)
            .Select(i =>
                timingData
                    .DriversByLap.Values.Select(x => x.GetValueOrDefault(number))
                    .Append(line)
                    .Select(x => x?.Sectors.GetValueOrDefault(i.ToString())?.ToTimeSpan())
                    .Where(x => x > TimeSpan.Zero)
                    .DefaultIfEmpty()
                    .Min()
            )
            .ToList();
        var ideal = bests.All(x => x is not null)
            ? PitWallTheme.LapTime(bests.Sum(x => x!.Value.TotalSeconds))
            : "–";
        var bestText = string.Join(
            " ",
            bests.Select(x =>
                x is null
                    ? "–"
                    : ShortSector(
                        x.Value.TotalSeconds.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    )
            )
        );

        return new Panel(
            new Rows(
                new Markup(
                    $"[grey]BEST[/] [bold]{line?.BestLapTime?.Value}[/]  [grey]SECTORS[/] {bestText}"
                ),
                new Markup($"[grey]IDEAL[/] [bold]{ideal}[/] [grey](best sectors added up)[/]"),
                new Text(""),
                table
            )
        )
        {
            Header = new PanelHeader(
                $"[black on #{hex}] {driver.RacingNumber} {driver.Tla} [/][bold #{hex}] P{line?.Line} [/]"
            ),
            Expand = true,
            BorderStyle = new Style(foreground: Color.FromHex(hex)),
        };
    }

    /// <summary>The latest race control messages, newest first, under the table.</summary>
    private IRenderable RaceControlPanel()
    {
        var offset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var rows = raceControl
            .Latest.Messages.Values.OrderByDescending(x => x.Utc)
            .Take(20)
            .Select(message =>
                (IRenderable)
                    new SingleLine(
                        $@"[grey]{new DateTimeOffset(message.Utc.DateTime, TimeSpan.Zero).ToOffset(offset):HH\:mm\:ss} {(message.Lap is null ? "   " : $"L{message.Lap, -2}")}[/] "
                            + $"[{PitWallCommsDisplay.MessageStyle(message).ToMarkup()}]{Markup.Escape(message.Message?.ToUpperInvariant() ?? "")}[/]"
                    )
            )
            .Prepend(new Markup($"[bold {Cyan}]RACE CONTROL[/]"))
            .ToList();
        return new Rows(rows);
    }

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
                    return best is null
                        ? ""
                        : ShortSector(
                            best.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        );
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
