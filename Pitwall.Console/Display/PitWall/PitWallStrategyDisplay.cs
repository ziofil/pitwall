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
    RaceControlMessageProcessor raceControl,
    PitStopSeriesProcessor pitStops,
    PitLaneTimeCollectionProcessor pitLaneTimes,
    LapCountProcessor lapCount,
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

        if (!sessionInfo.Latest.IsRace())
        {
            _image.Clear();
            return Task.FromResult(
                components.Page(
                    Screen,
                    new Layout("Strategy").SplitRows(
                        new Layout("Cars", cars).Size(RowsBelowTrace - 1),
                        new Layout("Notes", notes).Size(1),
                        new Layout(
                            "Trace",
                            new Text(" The race trace is drawn in races.", PitWallTheme.Dim)
                        )
                    )
                )
            );
        }

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
        var traces = RaceTrace();
        var focus = FocusCars(team);

        IRenderable chart;
        if (traces.Count == 0)
        {
            // Too early in the data for a trace (e.g. a recording joined mid-race, just started)
            _image.Clear();
            chart = new Text(" WAITING FOR COMPLETED LAPS", PitWallTheme.Dim);
        }
        else if (_image.Supported)
        {
            // Drawn as an image inside the panel's border, see PostContentDrawAsync
            var (columns, rows) = _image.WindowCells;
            // Each team's second car (by racing number) is dashed, as both share a colour
            var secondCars = traces
                .Keys.GroupBy(x => context.Driver(x).TeamName ?? x)
                .SelectMany(x => x.OrderBy(n => int.TryParse(n, out var i) ? i : 0).Skip(1))
                .ToHashSet();
            var stops = PitStopsByCar();
            var bands = NeutralisedPeriods(traces.Values.Max(x => x[^1].Lap));
            var lines = traces
                .Select(x => new TraceLine(
                    context.Driver(x.Key).Tla ?? x.Key,
                    (context.IsTeamCar(x.Key) ? team.Colour : context.Driver(x.Key).TeamColour)
                        ?? "FFFFFF",
                    context.IsTeamCar(x.Key) ? 2
                        : focus.Contains(x.Key) ? 1
                        : 0,
                    x.Value,
                    secondCars.Contains(x.Key),
                    stops.GetValueOrDefault(x.Key)
                ))
                .ToList();
            _image.Update(
                PitWallComponents.HeaderHeight + 1,
                1,
                columns - 2,
                rows - PitWallComponents.BodyRowsOffset - RowsBelowTrace - 2,
                (width, height) => ChartRenderers.RaceTrace(lines, bands, lapCount.Latest?.TotalLaps, width, height)
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
                "[bold] RACE TRACE [/][grey]seconds ahead of a car lapping at the field's median pace · team cars and nearest rivals highlighted · dots are stops in the new tyre's colour [/]"
            ),
            Expand = true,
            Padding = new Padding(0),
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }

    /// <summary>
    /// For each car, (lap, seconds ahead of a car lapping at the field's median pace), at the end
    /// of each lap and, where sector times allow, at each sector boundary in between.
    /// <para>
    /// The time the race leader crossed the line at the end of each lap is built up lap by lap:
    /// the leader's lap time, plus its gap to whoever led the lap before (zero if it was leading
    /// already). Each car's time is then the leader's time plus its gap to the leader. Gaps are
    /// in every lap's snapshot, so this works from whatever lap the data starts at (e.g. a
    /// recording joined mid-race) and copes with changes of leader.
    /// </para>
    /// <para>
    /// The reference adds up each lap's median lap time, so a safety car or rain slows it along
    /// with the field, instead of bending every line at once as a constant pace would. Sector
    /// points put a pit stop's time loss where it happened, so stops show as sharp drops.
    /// </para>
    /// </summary>
    internal Dictionary<string, List<(double Lap, double Delta)>> RaceTrace()
    {
        var laps = timingData
            .DriversByLap.Where(x => x.Value.Count > 0)
            .Select(x => x.Key)
            .Order()
            .ToList();
        if (laps.Count < 2)
            return [];

        // A normal lap here, to spot laps where the race was stopped
        var typicalLap = RaceStrategy.Median(
            laps.SelectMany(lap => timingData.DriversByLap[lap].Values)
                .Select(x => x.LastLapTime?.ToTimeSpan()?.TotalSeconds ?? 0)
                .Where(x => x > 0)
        );

        // Each lap's laps not through the pits, for the reference pace
        var cleanLaps = laps.ToDictionary(
            lap => lap,
            lap =>
                timingData
                    .DriversByLap[lap]
                    .Values.Where(x => !x.IsPitLap)
                    .Select(x => x.LastLapTime?.ToTimeSpan()?.TotalSeconds ?? 0)
                    .Where(x => x > 0)
                    .ToList()
        );

        // When the leader crossed the line at the end of each lap, and the reference car, from
        // the first lap we have; and the laps the race was stopped on, where gaps mean nothing
        var leaderTime = new Dictionary<int, double> { [laps[0]] = 0 };
        var reference = new Dictionary<int, double> { [laps[0]] = 0 };
        var sectorFractions = new Dictionary<int, (double S1, double S2)>();
        var stoppedLaps = new HashSet<int>();
        for (var i = 1; i < laps.Count; i++)
        {
            var lap = laps[i];
            var previousLap = laps[i - 1];
            var lines = timingData.DriversByLap[lap];
            var leader = lines.FirstOrDefault(x => x.Value.GapToLeaderSeconds() == 0);
            var lapTime = leader.Value?.LastLapTime?.ToTimeSpan()?.TotalSeconds;
            var previousGap = leader.Key is null
                ? null
                : (double?)timingData.DriversByLap[previousLap].SmartGapToLeaderSeconds(leader.Key);

            // The field's median lap, leaving out laps through the pits. When most of the field
            // pits on the same lap, the few left racing are no reference on their own, so it's
            // pooled with the laps either side
            var clean = cleanLaps[lap];
            var medianLap = RaceStrategy.Median(
                clean.Count >= Math.Max(5, lines.Count / 2)
                    ? clean
                    : Enumerable
                        .Range(lap - 2, 5)
                        .SelectMany(x => cleanLaps.GetValueOrDefault(x) ?? [])
                        .Where(x => x < typicalLap * 1.5)
            );

            // Where the sector boundaries fall in this lap, from its median sectors
            var racing = lines.Values.Where(x => !x.IsPitLap).ToList();
            double MedianSector(string sector) =>
                RaceStrategy.Median(
                    racing.Select(x => SectorSeconds(x, sector) ?? 0).Where(x => x > 0)
                );
            var (s1, s2, s3) = (MedianSector("0"), MedianSector("1"), MedianSector("2"));
            if (s1 > 0 && s2 > 0 && s3 > 0)
                sectorFractions[lap] = (s1 / (s1 + s2 + s3), (s1 + s2) / (s1 + s2 + s3));

            if (lap == previousLap + 1 && lapTime is > 0)
            {
                // A lap that includes a red flag stoppage counts as a normal lap: race traces
                // leave out neutralised time. Every car shifts equally, so gaps are unaffected.
                var stopped = lapTime.Value > typicalLap * 1.5;
                if (stopped)
                    stoppedLaps.Add(lap);
                var counted = stopped ? typicalLap : lapTime.Value;
                leaderTime[lap] = leaderTime[previousLap] + (previousGap ?? 0) + counted;
                reference[lap] =
                    reference[previousLap] + (stopped || medianLap <= 0 ? counted : medianLap);
            }
            else
            {
                // A gap in the data: carry on with a typical lap time for the missing laps
                var typical = medianLap > 0 ? medianLap : typicalLap;
                leaderTime[lap] = leaderTime[previousLap] + (typical * (lap - previousLap));
                reference[lap] = reference[previousLap] + (typical * (lap - previousLap));
            }
        }

        var traces = new Dictionary<string, List<(double Lap, double Delta)>>();
        foreach (var lap in laps.Where(x => !stoppedLaps.Contains(x)))
        {
            var lines = timingData.DriversByLap[lap];
            foreach (var (number, line) in lines)
            {
                if (lines.SmartGapToLeaderSeconds(number) is not { } gap)
                    continue;
                var time = leaderTime[lap] + (double)gap;
                var series = traces.GetValueOrDefault(number) ?? (traces[number] = []);

                // The sector boundaries of this lap, counted on from the end of the car's
                // previous lap, if that's in the trace and the sectors fit inside this lap
                if (
                    series.Count > 0
                    && series[^1].Lap == lap - 1
                    && sectorFractions.TryGetValue(lap, out var fractions)
                    && SectorSeconds(line, "0") is { } carS1
                    && SectorSeconds(line, "1") is { } carS2
                )
                {
                    var previousTime = reference[lap - 1] - series[^1].Delta;
                    if (previousTime + carS1 + carS2 < time)
                    {
                        double Reference(double fraction) =>
                            reference[lap - 1] + (fraction * (reference[lap] - reference[lap - 1]));
                        series.Add(
                            (
                                lap - 1 + fractions.S1,
                                Reference(fractions.S1) - (previousTime + carS1)
                            )
                        );
                        series.Add(
                            (
                                lap - 1 + fractions.S2,
                                Reference(fractions.S2) - (previousTime + carS1 + carS2)
                            )
                        );
                    }
                }
                series.Add((lap, reference[lap] - time));
            }
        }

        return traces.Where(x => x.Value.Count > 1).ToDictionary(x => x.Key, x => x.Value);
    }

    private static double? SectorSeconds(TimingDataPoint.Driver line, string sector) =>
        double.TryParse(
            line.Sectors.GetValueOrDefault(sector)?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds
        )
            ? seconds
            : null;

    /// <summary>
    /// Each car's pit stops: the lap its stop count went up on, and the tyre it was fitted with.
    /// </summary>
    internal Dictionary<string, List<TraceStop>> PitStopsByCar()
    {
        var stops = new Dictionary<string, List<TraceStop>>();
        var previous = new Dictionary<string, int>();
        foreach (var (lap, lines) in timingData.DriversByLap.OrderBy(x => x.Key))
        {
            foreach (var (number, line) in lines)
            {
                if (line.NumberOfPitStops is not { } count)
                    continue;
                if (previous.TryGetValue(number, out var before) && count > before)
                {
                    var compound = timingAppData
                        .Latest.Lines.GetValueOrDefault(number)
                        ?.Stints.GetValueOrDefault($"{count}")
                        ?.Compound;
                    (stops.GetValueOrDefault(number) ?? (stops[number] = [])).Add(
                        new TraceStop(lap, compound)
                    );
                }
                previous[number] = count;
            }
        }
        return stops;
    }

    /// <summary>
    /// When the race was neutralised (safety car, virtual safety car, red flag), in trace laps:
    /// lap <c>n</c> runs from <c>n - 1</c> to <c>n</c>. From race control's messages.
    /// </summary>
    internal List<TraceBand> NeutralisedPeriods(double lastLap)
    {
        var bands = new List<TraceBand>();
        (string Label, int Lap)? open = null;
        void Close(int lap)
        {
            if (open is { } period)
                bands.Add(new TraceBand(period.Lap - 1, Math.Max(lap, period.Lap), period.Label));
            open = null;
        }

        foreach (
            var message in raceControl
                .Latest.Messages.Values.Where(x => x.Lap is not null)
                .OrderBy(x => x.Utc)
        )
        {
            var text = message.Message?.ToUpperInvariant() ?? "";
            var lap = message.Lap!.Value;
            if (text.Contains("VIRTUAL SAFETY CAR DEPLOYED"))
            {
                Close(lap - 1);
                open = ("VSC", lap);
            }
            else if (text.Contains("VIRTUAL SAFETY CAR ENDING") && open?.Label == "VSC")
                Close(lap);
            else if (text.Contains("SAFETY CAR DEPLOYED"))
            {
                Close(lap - 1);
                open = ("SC", lap);
            }
            else if (text.Contains("SAFETY CAR IN THIS LAP") && open?.Label == "SC")
                Close(lap);
            else if (text.Contains("RED FLAG"))
            {
                Close(lap - 1);
                open = ("RED FLAG", lap);
            }
            else if (
                open?.Label == "RED FLAG"
                && (text.Contains("STANDING START") || text.Contains("ROLLING START"))
            )
                Close(lap);
        }
        Close((int)Math.Ceiling(lastLap));
        return bands;
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
        // The whole race, from the start to the flag, as in the image version
        const double minLap = 0;
        var lastLap = traces.Values.Max(x => x[^1].Lap);
        var maxLap = Math.Max(Math.Max(lapCount.Latest?.TotalLaps ?? 0, Math.Ceiling(lastLap)), 5);
        // Scale to the whole field so every car is on the chart, leaving out the deepest dips
        // (pit stops and red flags) so they don't squash everything else
        var focusValues = traces
            .Values.SelectMany(x => x.Select(p => p.Delta))
            .DefaultIfEmpty(0)
            .Order()
            .ToList();
        var high = focusValues[^1];
        var low = focusValues[(int)(focusValues.Count * 0.03)];
        var rows = Math.Max(1, canvas.Rows - 1);
        // A round step that keeps the seconds labels at least two rows apart
        var step = new[] { 5.0, 10, 20, 50, 100, 200 }.FirstOrDefault(
            x => (high - low + 10) / x <= rows / 2.0,
            500
        );
        var maxY = Math.Max(step / 2, Math.Ceiling(high / step) * step + (step / 2));
        var minY = Math.Floor(low / step) * step - (step / 2);
        var yLabels = Enumerable
            .Range(0, (int)((maxY - minY) / step) + 2)
            .Select(i => (Math.Ceiling(minY / step) * step) + (i * step))
            .Where(y => y <= maxY)
            .ToList();
        var gutter = yLabels.Max(y => $"{y:+0;-0;0}".Length) + 1;

        // Leave space on the left for the seconds, on the right for driver labels, and the
        // bottom row for lap numbers
        var plot = new BrailleCanvas(
            Math.Max(1, canvas.Columns - labelColumns - gutter),
            rows
        );
        var rangeX = maxLap - minLap;

        // Grid: at each labelled second and every 5 laps
        foreach (var y in yLabels)
        {
            var py = (int)Math.Round((1 - ((y - minY) / (maxY - minY))) * (plot.Height - 1));
            plot.HorizontalGuide(py, PitWallTheme.Grid);
        }
        for (var lap = (int)Math.Ceiling(minLap / 5) * 5; lap <= maxLap; lap += 5)
        {
            plot.VerticalGuide(
                (int)Math.Round((lap - minLap) / rangeX * (plot.Width - 1)),
                PitWallTheme.Grid
            );
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
            plot.Plot(points, minLap, maxLap, minY, maxY, colour);
        }

        CopyInto(canvas, plot, gutter * 2);

        // Seconds in the gutter, right-aligned against the plot
        foreach (var y in yLabels)
        {
            var row = (int)Math.Round((1 - ((y - minY) / (maxY - minY))) * (plot.Height - 1)) / 4;
            var text = $"{y:+0;-0;0}";
            canvas.Label(gutter - 1 - text.Length, row, text, PitWallTheme.Dim);
        }

        // Lap numbers along the bottom, every 5 laps or every 10 if they'd crowd
        var lapStep = plot.Columns * 5 / rangeX < 5 ? 10 : 5;
        for (var lap = (int)Math.Ceiling(minLap / lapStep) * lapStep; lap <= maxLap; lap += lapStep)
        {
            var column = gutter + ((int)Math.Round((lap - minLap) / rangeX * (plot.Width - 1)) / 2);
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
            // Just after the line ends, which is the right edge only by the end of the race
            var endColumn = gutter + ((int)Math.Round(lastLap / rangeX * (plot.Width - 1)) / 2) + 1;
            canvas.Label(
                Math.Min(endColumn, gutter + plot.Columns),
                row,
                $" {context.Driver(number).Tla}",
                new Style(foreground: colour, decoration: Decoration.Bold)
            );
        }
    }

    private static void CopyInto(BrailleCanvas target, BrailleCanvas source, int offsetX)
    {
        // Re-plots the source dots onto the larger target canvas, keeping colours
        for (var x = 0; x < source.Width; x++)
        {
            for (var y = 0; y < source.Height; y++)
            {
                if (source.Get(x, y) is { } colour)
                    target.Set(x + offsetX, y, colour);
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
                return $"[grey]{x.Lap}[/]{pit} [{style.ToMarkup()}]{x.Line.LastLapTime?.Value ?? "–"}[/]";
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
                $"[grey]L{x.Lap}[/] lane {PitTime(x.PitLaneTime)} stop {PitTime(x.PitStopTime)}"
            )
            .ToList();
        stops ??= pitLaneTimes
            .Latest.PitTimesList.GetValueOrDefault(number)
            ?.Select(x => $"[grey]L{x.Lap}[/] lane {PitTime(x.Duration)}")
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

    /// <summary>
    /// A pit lane or stop time in seconds as shown, e.g. <c>22.6s</c>. Times of minutes are cars
    /// held in the pit lane during a red flag, shown as <c>38:34 red flag</c>.
    /// </summary>
    private static string PitTime(string? seconds) =>
        !double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? $"[bold]{Markup.Escape(seconds ?? "–")}[/]"
        : value >= 120 ? $"[bold]{TimeSpan.FromSeconds(value):m\\:ss}[/] [grey]red flag[/]"
        : $"[bold]{value:0.0}s[/]";

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
        if (history.Count == 0)
            return new SingleLine($"[grey]{label}–[/]");
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
