using Pitwall.Console.Graphics;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// The strategist's page: a race trace (each car's cumulative time against a reference pace, as
/// teams use to read pace, pit stops and gaps at a glance), and for each of the team's cars the
/// tyre, pace and degradation, the gaps around it with their trend, where it would rejoin if it
/// pitted now, and which rivals are in undercut range.
/// </summary>
public sealed class PitWallStrategyDisplay(
    PitWallComponents components,
    PitWallContext context,
    TimingDataProcessor timingData,
    TimingAppDataProcessor timingAppData,
    TimingSmoother smoother,
    SessionInfoProcessor sessionInfo,
    TrackStatusProcessor trackStatus,
    PitStopSeriesProcessor pitStops,
    PitLaneTimeCollectionProcessor pitLaneTimes,
    ITerminalImages images
) : IDisplay
{
    /// <summary>Rows taken by the car panels and the notes line under the race trace.</summary>
    private const int RowsBelowTrace = 13;

    private readonly InlineImage _image = new(images);

    /// <summary>A rival within this many seconds could gain the place with an earlier stop.</summary>
    public const double UndercutRangeSeconds = 3.0;

    private const int TrendLaps = 4;

    public Screen Screen => Screen.PitWallStrategy;

    // Smoothed values glide between feed updates, so redraw quickly to show the motion; when
    // drawing the race trace as an image, its cost sets the pace
    public TimeSpan FrameInterval =>
        _image.Supported ? _image.FrameInterval : TimeSpan.FromMilliseconds(66);

    public Task PostContentDrawAsync(bool forceDraw) => _image.DrawAsync(forceDraw);

    public Task<IRenderable> GetContentAsync()
    {
        var team = context.Team;
        if (team is null || timingData.Latest.Lines.Count == 0)
        {
            _image.Clear();
            return Task.FromResult(
                components.Page(Screen, new Text("NO TEAM OR TIMING DATA", PitWallTheme.Dim))
            );
        }

        var circuitPitLoss = sessionInfo.Latest.PitLoss;
        var pitLoss = RaceStrategy.ForTrackStatus(
            RaceStrategy.EstimatePitLoss(timingData.DriversByLap, circuitPitLoss),
            circuitPitLoss,
            trackStatus.Latest?.Status
        );
        var lines = timingData.Latest.GetOrderedLines();

        var cars = new Layout("Cars").SplitColumns(
            team.DriverNumbers.Select(number => new Layout(
                    number,
                    CarPanel(number, lines, pitLoss)
                ))
                .ToArray()
        );

        var notes = new SingleLine(
            $"[grey]PIT LOSS[/] [bold]{pitLoss.Seconds:0.0}s[/] "
                + pitLoss.Source switch
                {
                    PitLossSource.Observed => $"[grey](median of {pitLoss.Samples} stops)[/]",
                    PitLossSource.Circuit => "[grey](circuit typical, no stops seen yet)[/]",
                    PitLossSource.SafetyCar =>
                        "[black on #ffe600] SAFETY CAR [/] [grey]cheaper stop[/]",
                    PitLossSource.VirtualSafetyCar =>
                        "[black on #ffe600] VSC [/] [grey]cheaper stop[/]",
                    _ => "[grey](generic, no stops or circuit data)[/]",
                }
                + "[grey]  ·  REJOIN assumes no other car stops  ·  UNDERCUT RANGE[/] "
                + $"[bold]{UndercutRangeSeconds:0.0}s[/]"
        );

        var body = new Layout("Strategy").SplitRows(
            new Layout("Trace", RaceTracePanel(team)),
            // Nine rows of detail per car, plus the panel border
            new Layout("Cars", cars).Size(RowsBelowTrace - 1),
            new Layout("Notes", notes).Size(1)
        );

        return Task.FromResult(components.Page(Screen, body));
    }

    private IRenderable RaceTracePanel(PitWallTeam team)
    {
        if (!sessionInfo.Latest.IsRace())
        {
            _image.Clear();
            return new Panel(new Text("RACE TRACE IS ONLY AVAILABLE IN RACES", PitWallTheme.Dim))
            {
                Header = new PanelHeader("[bold] RACE TRACE [/]"),
                Expand = true,
                BorderStyle = new Style(foreground: PitWallTheme.Grid),
            };
        }

        var traces = RaceTrace();
        var focus = FocusCars(team);

        IRenderable chart;
        if (_image.Supported)
        {
            // Drawn as an image inside the panel's border, see PostContentDrawAsync
            var (columns, rows) = _image.WindowCells;
            var lines = traces
                .Select(x => new TraceLine(
                    context.Driver(x.Key).Tla ?? x.Key,
                    (context.IsTeamCar(x.Key) ? team.Colour : context.Driver(x.Key).TeamColour)
                        ?? "FFFFFF",
                    context.IsTeamCar(x.Key) ? 2
                        : focus.Contains(x.Key) ? 1
                        : 0,
                    x.Value
                ))
                .ToList();
            _image.Update(
                PitWallComponents.HeaderHeight + 1,
                1,
                columns - 2,
                rows - PitWallComponents.BodyRowsOffset - RowsBelowTrace - 2,
                (width, height) => ChartRenderers.RaceTrace(lines, width, height)
            );
            chart = new Text("");
        }
        else
        {
            chart = new CanvasRenderable(
                canvas => DrawRaceTrace(canvas, traces, focus, team),
                defaultRows: 16
            );
        }

        return new Panel(chart)
        {
            Header = new PanelHeader(
                "[bold] RACE TRACE [/][grey]time vs. reference pace (s) · team cars and their nearest rivals highlighted [/]"
            ),
            Expand = true,
            Padding = new Padding(0),
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }

    /// <summary>
    /// For each car, (lap, seconds ahead of a car running at the reference pace). The first lap
    /// has no lap time in the feed, so each car starts from its gap to the leader after lap 1 and
    /// adds its lap times from there. The reference is the leader's average lap since lap 1, so
    /// the leader ends near zero.
    /// </summary>
    private Dictionary<string, List<(double Lap, double Delta)>> RaceTrace()
    {
        var firstLap = timingData.DriversByLap.GetValueOrDefault(1);
        if (firstLap is null)
            return [];

        // Race time since the leader crossed the line at the end of lap 1
        var elapsed = new Dictionary<string, List<(int Lap, double Time)>>();
        foreach (var number in firstLap.Keys)
        {
            if (firstLap.SmartGapToLeaderSeconds(number) is { } gap)
                elapsed[number] = [(1, (double)gap)];
        }

        foreach (var lap in timingData.DriversByLap.Keys.Where(x => x > 1).Order())
        {
            foreach (var (number, line) in timingData.DriversByLap[lap])
            {
                var time = line.LastLapTime?.ToTimeSpan()?.TotalSeconds;
                // Only extend a series with consecutive laps, a missed lap would skew it
                if (
                    time is > 0
                    && elapsed.TryGetValue(number, out var series)
                    && series[^1].Lap == lap - 1
                )
                    series.Add((lap, series[^1].Time + time.Value));
            }
        }

        var leader = timingData.Latest.GetOrderedLines().Keys.FirstOrDefault();
        var leaderSeries = leader is null ? null : elapsed.GetValueOrDefault(leader);
        if (leaderSeries is null || leaderSeries.Count < 2)
            return [];
        var reference = (leaderSeries[^1].Time - leaderSeries[0].Time) / (leaderSeries[^1].Lap - 1);

        return elapsed
            .Where(x => x.Value.Count > 1)
            .ToDictionary(
                x => x.Key,
                x =>
                    x.Value.Select(p => ((double)p.Lap, ((p.Lap - 1) * reference) - p.Time))
                        .ToList()
            );
    }

    /// <summary>The team's cars and the cars directly ahead of and behind each of them.</summary>
    private HashSet<string> FocusCars(PitWallTeam team)
    {
        var ordered = timingData.Latest.GetOrderedLines().Keys.ToList();
        var focus = new HashSet<string>();
        foreach (var number in team.DriverNumbers)
        {
            var index = ordered.IndexOf(number);
            if (index < 0)
                continue;
            focus.Add(number);
            if (index > 0)
                focus.Add(ordered[index - 1]);
            if (index < ordered.Count - 1)
                focus.Add(ordered[index + 1]);
        }
        return focus;
    }

    private void DrawRaceTrace(
        BrailleCanvas canvas,
        Dictionary<string, List<(double Lap, double Delta)>> traces,
        HashSet<string> focus,
        PitWallTeam team
    )
    {
        if (traces.Count == 0)
        {
            canvas.Label(1, 0, "WAITING FOR COMPLETED LAPS", PitWallTheme.Dim);
            return;
        }

        const int labelColumns = 5;
        var maxLap = Math.Max(5, traces.Values.Max(x => x[^1].Lap));
        // Scale to the focus cars so the interesting part of the field fills the chart
        var focusValues = traces
            .Where(x => focus.Contains(x.Key))
            .SelectMany(x => x.Value.Select(p => p.Delta))
            .DefaultIfEmpty(0)
            .ToList();
        var maxY = Math.Max(5, Math.Ceiling(focusValues.Max() / 10) * 10 + 5);
        var minY = Math.Floor(focusValues.Min() / 10) * 10 - 5;

        // Leave space on the right for driver labels, and the bottom row for lap numbers
        var plot = new BrailleCanvas(canvas.Columns - labelColumns, canvas.Rows - 1);
        var rangeX = maxLap;

        // Grid: every 10 seconds and every 5 laps
        for (var y = Math.Ceiling(minY / 10) * 10; y <= maxY; y += 10)
        {
            var py = (int)Math.Round((1 - ((y - minY) / (maxY - minY))) * (plot.Height - 1));
            plot.HorizontalGuide(py, PitWallTheme.Grid);
        }
        for (var lap = 5; lap <= maxLap; lap += 5)
        {
            plot.VerticalGuide((int)Math.Round(lap / rangeX * (plot.Width - 1)), PitWallTheme.Grid);
        }

        // Background cars first, then highlighted cars so they draw on top
        foreach (
            var (number, points) in traces
                .OrderBy(x => focus.Contains(x.Key))
                .ThenBy(x => context.IsTeamCar(x.Key))
        )
        {
            var colour =
                context.IsTeamCar(number) ? PitWallTheme.TeamColour(team.Colour)
                : focus.Contains(number)
                    ? PitWallTheme.TeamColour(context.Driver(number).TeamColour)
                : new Color(55, 55, 62);
            plot.Plot(points, 0, maxLap, minY, maxY, colour);
        }

        CopyInto(canvas, plot);

        // Gap labels on the left edge of the grid lines
        for (var y = Math.Ceiling(minY / 10) * 10; y <= maxY; y += 10)
        {
            var row = (int)Math.Round((1 - ((y - minY) / (maxY - minY))) * (plot.Height - 1)) / 4;
            canvas.Label(0, row, $"{y:0}", PitWallTheme.Dim);
        }

        // Lap numbers along the bottom
        for (var lap = 5; lap <= maxLap; lap += 5)
        {
            var column = (int)Math.Round(lap / rangeX * (plot.Width - 1)) / 2;
            canvas.Label(Math.Max(0, column - 1), canvas.Rows - 1, $"{lap}", PitWallTheme.Dim);
        }

        // Driver labels at the end of each focus car's line
        var usedRows = new HashSet<int>();
        foreach (var number in focus.OrderBy(x => !context.IsTeamCar(x)))
        {
            if (!traces.TryGetValue(number, out var points))
                continue;
            var y = Math.Clamp(points[^1].Delta, minY, maxY);
            var row = (int)Math.Round((1 - ((y - minY) / (maxY - minY))) * (plot.Height - 1)) / 4;
            while (usedRows.Contains(row) && row < canvas.Rows - 2)
                row++;
            usedRows.Add(row);
            var colour = context.IsTeamCar(number)
                ? PitWallTheme.TeamColour(team.Colour)
                : PitWallTheme.TeamColour(context.Driver(number).TeamColour);
            canvas.Label(
                plot.Columns,
                row,
                $" {context.Driver(number).Tla}",
                new Style(foreground: colour, decoration: Decoration.Bold)
            );
        }
    }

    private static void CopyInto(BrailleCanvas target, BrailleCanvas source)
    {
        // Re-plots the source dots onto the larger target canvas, keeping colours
        for (var x = 0; x < source.Width; x++)
        {
            for (var y = 0; y < source.Height; y++)
            {
                if (source.Get(x, y) is { } colour)
                    target.Set(x, y, colour);
            }
        }
    }

    private IRenderable CarPanel(
        string number,
        Dictionary<string, TimingDataPoint.Driver> lines,
        PitLossEstimate pitLoss
    )
    {
        var driver = context.Driver(number);
        var line = lines.GetValueOrDefault(number) ?? new();
        var teamColour = PitWallTheme.TeamColour(driver.TeamColour);
        var hex = teamColour.ToHex();
        var ordered = lines.Keys.ToList();
        var index = ordered.IndexOf(number);
        var ahead = index > 0 ? ordered[index - 1] : null;
        var behind = index >= 0 && index < ordered.Count - 1 ? ordered[index + 1] : null;

        var rows = new List<IRenderable>();

        if (line.Retired == true || line.Stopped == true)
        {
            rows.Add(new SingleLine("[bold red]RETIRED[/]"));
            return CarPanelFrame(driver, line, hex, rows);
        }

        // Tyre and stint
        var app = timingAppData.Latest.Lines.GetValueOrDefault(number);
        var stint = app?.Stints.OrderBy(x => int.Parse(x.Key)).LastOrDefault().Value;
        var stintStartLap =
            app is null || stint is null ? 0 : (line.NumberOfLaps ?? 0) - stint.GetStintDuration();
        rows.Add(
            new SingleLine(
                $"[grey]TYRE  [/] {PitWallTheme.CompoundMarkup(stint?.Compound)} "
                    + $"[bold]{Markup.Escape(stint?.Compound ?? "UNKNOWN")}[/] "
                    + $"[grey]age[/] [bold]{stint?.TotalLaps}[/] "
                    + $"[grey]({(stint?.New == true ? "new" : "used")}, stint {app?.Stints.Count})  stops[/] [bold]{line.NumberOfPitStops ?? 0}[/]"
            )
        );

        // Pace and degradation over the current stint
        var stintLaps = RaceStrategy.GetCleanLaps(
            timingData.DriversByLap,
            number,
            stintStartLap + 1
        );
        var lastFive = stintLaps.TakeLast(5).ToList();
        var degradation = RaceStrategy.Slope(
            stintLaps.Select(x => ((double)x.Lap, x.Seconds)).ToList()
        );
        var degradationText = degradation is null
            ? "[grey]–[/]"
            : $"[{(degradation > 0.05 ? "#ff8c00" : "white")}]{PitWallTheme.Gap(degradation, 3)} s/lap[/]";
        rows.Add(
            new SingleLine(
                $"[grey]PACE  [/] last [bold]{line.LastLapTime?.Value}[/]  "
                    + $"[grey]avg5[/] [bold]{PitWallTheme.LapTime(lastFive.Count == 0 ? null : lastFive.Average(x => x.Seconds))}[/]  "
                    + $"[grey]deg[/] {degradationText}"
            )
        );

        // Gaps to the cars around, with the trend over the last few laps
        rows.Add(
            GapRow(
                "AHEAD ",
                ahead,
                ahead is null ? null : SmoothInterval(number, line),
                ahead,
                number
            )
        );
        var behindLine = behind is null ? null : lines.GetValueOrDefault(behind);
        rows.Add(
            GapRow(
                "BEHIND",
                behind,
                behind is null || behindLine is null ? null : SmoothInterval(behind, behindLine),
                number,
                behind
            )
        );

        // Where the car would rejoin if it pitted this lap
        var rejoin = RaceStrategy.PredictRejoin(lines, number, pitLoss.Seconds);
        if (rejoin is not null)
        {
            var positionChange = rejoin.Position - (line.Line ?? rejoin.Position);
            var aheadText = rejoin.AheadDriverNumber is null
                ? ""
                : $" [grey]behind[/] {DriverMarkup(rejoin.AheadDriverNumber)} [grey]by[/] {rejoin.GapToAhead:0.0}s";
            var behindText = rejoin.BehindDriverNumber is null
                ? ""
                : $" [grey]·[/] {DriverMarkup(rejoin.BehindDriverNumber)} [grey]{rejoin.GapToBehind:0.0}s back[/]";
            var trafficWarning = rejoin.GapToAhead < 1.5 ? " [black on #ffe600] TRAFFIC [/]" : "";
            rows.Add(
                new SingleLine(
                    $"[grey]PIT NOW[/] [bold]P{rejoin.Position}[/] [grey]({(positionChange > 0 ? "-" : "+")}{Math.Abs(positionChange)})[/]{aheadText}{behindText}{trafficWarning}"
                )
            );
        }

        // Undercut range: cars within range who could jump us, or who we could jump
        var threats = new List<string>();
        if (
            behind is not null
            && behindLine?.IntervalToPositionAhead?.IntervalSeconds() is { } behindGap
            && (double)behindGap < UndercutRangeSeconds
        )
        {
            threats.Add($"[#ff3333]THREAT[/] {DriverMarkup(behind)} {behindGap:0.0}s");
        }
        if (
            ahead is not null
            && line.IntervalToPositionAhead?.IntervalSeconds() is { } aheadGap
            && (double)aheadGap < UndercutRangeSeconds
        )
        {
            threats.Add($"[#00dc00]CHANCE[/] {DriverMarkup(ahead)} {aheadGap:0.0}s");
        }
        rows.Add(
            new SingleLine(
                "[grey]UNDERCUT[/] "
                    + (threats.Count == 0 ? "[grey]nobody in range[/]" : string.Join("  ", threats))
            )
        );

        // Recent laps, coloured like the timing monitor
        var recent = timingData
            .DriversByLap.Where(x => x.Key > 1)
            .OrderByDescending(x => x.Key)
            .Take(5)
            .Select(x => (Lap: x.Key, Line: x.Value.GetValueOrDefault(number)))
            .Where(x => x.Line is not null)
            .Reverse()
            .Select(x =>
            {
                var style = PitWallTheme.TimeStyle(x.Line!.LastLapTime, improvedOnly: true);
                var pit = x.Line.IsPitLap ? "[#ff3333]P[/]" : " ";
                return $"[grey]{x.Lap}[/]{pit}[{style.ToMarkup()}]{x.Line.LastLapTime?.Value}[/]";
            });
        rows.Add(new SingleLine("[grey]LAPS  [/] " + string.Join(" ", recent)));

        // Gap history to the cars around, oldest first
        rows.Add(GapHistoryRow("GAP ▲ ", ahead, number));
        rows.Add(GapHistoryRow("GAP ▼ ", number, behind));
        rows.Add(StopsRow(number));

        return CarPanelFrame(driver, line, hex, rows);
    }

    private static Panel CarPanelFrame(
        DriverListDataPoint.Driver driver,
        TimingDataPoint.Driver line,
        string hex,
        List<IRenderable> rows
    ) =>
        new(new Rows(rows))
        {
            Header = new PanelHeader(
                $"[black on #{hex}] {driver.RacingNumber} {driver.Tla} [/][bold #{hex}] P{line.Line} [/]"
            ),
            Expand = true,
            BorderStyle = new Style(foreground: Color.FromHex(hex)),
        };

    /// <summary>
    /// The car's pit stops so far: lap, time in the pit lane and stationary time. The stop
    /// series has both times; without it, the pit lane times alone are shown. Both feeds need an
    /// F1 TV login during live sessions.
    /// </summary>
    private IRenderable StopsRow(string number)
    {
        var stops = pitStops
            .Latest.PitTimes.GetValueOrDefault(number)
            ?.OrderBy(x => int.TryParse(x.Key, out var i) ? i : 0)
            .Select(x => x.Value.PitStop)
            .Select(x =>
                $"[grey]L{x.Lap}[/] lane [bold]{x.PitLaneTime}s[/] stop [bold]{x.PitStopTime}s[/]"
            )
            .ToList();
        stops ??= pitLaneTimes
            .Latest.PitTimesList.GetValueOrDefault(number)
            ?.Select(x => $"[grey]L{x.Lap}[/] lane [bold]{x.Duration}s[/]")
            .ToList();

        return new SingleLine(
            "[grey]STOPS [/] "
                + (
                    stops is null || stops.Count == 0
                        ? "[grey]no stop times (needs an F1 TV login live)[/]"
                        : string.Join("  ", stops.TakeLast(3))
                )
        );
    }

    /// <summary>The interval to the car ahead, interpolated between feed updates.</summary>
    private double? SmoothInterval(string number, TimingDataPoint.Driver line) =>
        line.IntervalToPositionAhead?.IntervalSeconds() is > 0
            ? smoother.Interval(number) ?? (double?)line.IntervalToPositionAhead.IntervalSeconds()
            : (double?)line.IntervalToPositionAhead?.IntervalSeconds();

    private IRenderable GapRow(
        string label,
        string? rival,
        double? gap,
        string? ahead,
        string? behind
    )
    {
        if (rival is null || ahead is null || behind is null)
            return new SingleLine($"[grey]{label}[/] [grey]–[/]");

        var history = RaceStrategy.GetGapHistory(
            timingData.DriversByLap,
            ahead,
            behind,
            TrendLaps + 1
        );
        var trend = RaceStrategy.Slope(history.Select(x => ((double)x.Lap, x.Gap)).ToList());

        // For the car ahead a shrinking gap is good, for the car behind a growing gap is good
        var isAhead = label.Trim() == "AHEAD";
        var trendText =
            trend is null ? ""
            : (trend < 0) == isAhead
                ? $"[#00dc00]{(isAhead ? "closing" : "pulling away")} {Math.Abs(trend.Value):0.00}s/lap[/]"
            : $"[#ff3333]{(isAhead ? "dropping back" : "being caught")} {Math.Abs(trend.Value):0.00}s/lap[/]";

        var rivalLine = timingData.Latest.Lines.GetValueOrDefault(rival);
        var rivalStint = timingAppData
            .Latest.Lines.GetValueOrDefault(rival)
            ?.Stints.OrderBy(x => int.Parse(x.Key))
            .LastOrDefault()
            .Value;
        var tyre = rivalStint is null
            ? ""
            : $"{PitWallTheme.CompoundMarkup(rivalStint.Compound)}[grey]{rivalStint.TotalLaps}[/]";

        return new SingleLine(
            $"[grey]{label}[/] [grey]P{rivalLine?.Line}[/] {DriverMarkup(rival)} [bold]{(gap is null ? "–" : $"{gap:0.000}")}[/] {tyre}  {trendText}"
        );
    }

    private IRenderable GapHistoryRow(string label, string? ahead, string? behind)
    {
        if (ahead is null || behind is null)
            return new SingleLine($"[grey]{label}–[/]");
        var history = RaceStrategy.GetGapHistory(timingData.DriversByLap, ahead, behind, 6);
        var values = history.Select(x =>
            x.Gap is >= 0 and < UndercutRangeSeconds ? $"[#ff8c00]{x.Gap:0.0}[/]" : $"{x.Gap:0.0}"
        );
        // Negative gaps are laps where the order was the other way round
        return new SingleLine(
            $"[grey]{label}[/]{string.Join(" [grey]›[/] ", values)} [grey](last {history.Count} laps)[/]"
        );
    }

    private string DriverMarkup(string number)
    {
        var driver = context.Driver(number);
        return $"[bold {PitWallTheme.TeamColour(driver.TeamColour).ToMarkup()}]{driver.Tla}[/]";
    }
}
