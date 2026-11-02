using Pitwall.Console.Graphics;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// A telemetry page in the style of the waveform displays engineers use (as in McLaren Applied's
/// ATLAS): per car, a legend of live channel values, then stacked traces against lap distance
/// with the current lap drawn over the previous one. Only the channels in the public feed are
/// available: speed, RPM, gear, throttle, brake and DRS.
/// </summary>
public sealed class PitWallTelemetryDisplay(
    PitWallComponents components,
    PitWallContext context,
    CarDataProcessor carData,
    CarTelemetryHistoryProcessor telemetry,
    TimingStatsProcessor timingStats,
    ITerminalImages images
) : IDisplay
{
    // Rows of each car's column above and below the trace strips: title bar and legend, and
    // the distance axis; plus the status bar under the columns
    private const int RowsAboveStrips = 6;
    private const int RowsBelowStrips = 2;
    private const int LabelColumns = 11;

    private readonly InlineImage _image = new(images);

    private static readonly Color TitleBar = new(43, 43, 43);
    private static readonly Color StatusBar = new(30, 127, 216);
    private static readonly Color PreviousLap = new(85, 85, 95);

    private static readonly (
        string Name,
        string Unit,
        Color Colour,
        Func<TelemetrySample, double> Value,
        double Min,
        double Max
    )[] Channels =
    [
        ("vCar", "kph", new Color(51, 153, 255), s => s.Speed, 0, 360),
        ("rThrottle", "%", new Color(0, 220, 0), s => s.Throttle, 0, 100),
        ("pBrake", "", new Color(255, 40, 40), s => s.Brake > 0 ? 1 : 0, 0, 1),
        ("NGear", "", new Color(255, 230, 0), s => s.Gear, 0, 8),
        ("nEngine", "rpm", new Color(220, 80, 255), s => s.Rpm, 4000, 13000),
        ("DRS", "", new Color(0, 200, 230), s => s.DrsOpen ? 1 : 0, 0, 1),
    ];

    public Screen Screen => Screen.PitWallTelemetry;

    // Speed, throttle, brake and so on drawn as images: weights set how much height each gets
    private static readonly TelemetryChannel[] ImageChannels =
    [
        .. Channels.Select(channel => new TelemetryChannel(
            channel.Name,
            channel.Unit,
            channel.Colour.ToHex(),
            channel.Value,
            channel.Min,
            channel.Max,
            channel.Name switch
            {
                "vCar" => 4,
                "pBrake" or "DRS" => 1,
                _ => 2,
            }
        )),
    ];

    // Smoothed values glide between feed updates, so redraw quickly to show the motion; when
    // drawing the traces as an image, its cost sets the pace
    public TimeSpan FrameInterval =>
        _image.Supported ? _image.FrameInterval : TimeSpan.FromMilliseconds(66);

    public Task PostContentDrawAsync(bool forceDraw) => _image.DrawAsync(forceDraw);

    public Task<IRenderable> GetContentAsync()
    {
        var team = context.Team;
        if (team is null)
        {
            _image.Clear();
            return Task.FromResult(
                components.Page(Screen, new Text("NO TEAM SELECTED", PitWallTheme.Dim))
            );
        }

        var hasData = team.DriverNumbers.Any(x => telemetry.GetLaps(x).Count > 0);
        if (hasData && _image.Supported)
        {
            // Both cars' trace strips as one image, drawn over the empty strip area of the columns
            var cars = team
                .DriverNumbers.Select(number => LapsFor(number))
                .Select(x => new TelemetryCar(x.Current, x.Previous))
                .ToList();
            var (columns, rows) = _image.WindowCells;
            _image.Update(
                PitWallComponents.HeaderHeight + RowsAboveStrips,
                0,
                columns,
                rows - PitWallComponents.BodyRowsOffset - RowsAboveStrips - RowsBelowStrips,
                (width, height) =>
                    ChartRenderers.Telemetry(
                        cars,
                        ImageChannels,
                        width,
                        height,
                        LabelColumns * width / (float)columns
                    )
            );
        }
        else
        {
            _image.Clear();
        }

        IRenderable body = hasData
            ? new Layout("Cars").SplitColumns(
                team.DriverNumbers.Select(number => new Layout(number, CarWaveform(number)))
                    .ToArray()
            )
            : new Markup(
                """
                [yellow]No car telemetry received yet.[/]

                Live car telemetry requires an F1 TV subscription. Log in with [bold]pitwall login[/] to receive it.
                Recorded and imported sessions include telemetry once a few seconds have been replayed.
                """
            );

        var layout = new Layout("Telemetry").SplitRows(
            new Layout("Body", body),
            new Layout("Status", StatusLine()).Size(1)
        );
        return Task.FromResult(components.Page(Screen, layout));
    }

    /// <summary>The lap in progress, drawn up to the car's interpolated position, and the lap before.</summary>
    private (TelemetryLap? Current, TelemetryLap? Previous) LapsFor(string number)
    {
        var laps = telemetry.GetLaps(number);
        var current = laps.Count > 0 ? laps[^1] : null;

        // Draw the current lap up to the interpolated live position, so the trace and the cursor
        // advance continuously rather than in the feed's once-a-second batches
        if (
            current is not null
            && telemetry.TryGetLive(number, out var live)
            && live.Distance <= current.Distance
        )
        {
            current = current with
            {
                Samples = [.. current.Samples.Where(x => x.Distance < live.Distance), live],
            };
        }
        return (current, laps.Count > 1 ? laps[^2] : null);
    }

    private Layout CarWaveform(string number)
    {
        var driver = context.Driver(number);
        var (current, previous) = LapsFor(number);
        var teamHex = PitWallTheme.TeamColour(driver.TeamColour).ToHex();

        var title = new SingleLine(
            $"[white] [#{teamHex}]■[/] Waveform · {driver.RacingNumber} {driver.Tla} · Lap {current?.Lap}"
                + (previous is null ? "" : $" [grey]vs Lap {previous.Lap}[/]")
                + $" · {current?.Distance ?? 0:0} m[/]",
            TitleBar
        );

        // Scale the distance axis to a full lap: the previous lap's length if we have it
        var lapLength = Math.Max(previous?.Distance ?? 0, current?.Distance ?? 0);
        if (lapLength < 100)
            lapLength = 100;

        var strips = Channels
            .Select(channel =>
                new Layout(channel.Name)
                    .SplitColumns(
                        new Layout(
                            "Label",
                            ChannelLabel(channel.Name, channel.Unit, channel.Colour)
                        ).Size(LabelColumns),
                        new Layout(
                            "Trace",
                            new CanvasRenderable(canvas =>
                            {
                                canvas.HorizontalGuide(canvas.Height - 1, PitWallTheme.Grid);
                                if (previous is not null)
                                {
                                    canvas.Plot(
                                        Points(previous, channel.Value),
                                        0,
                                        lapLength,
                                        channel.Min,
                                        channel.Max,
                                        PreviousLap
                                    );
                                }
                                if (current is not null)
                                {
                                    canvas.Plot(
                                        Points(current, channel.Value),
                                        0,
                                        lapLength,
                                        channel.Min,
                                        channel.Max,
                                        channel.Colour
                                    );
                                    // Cursor at the car's current position on the lap
                                    canvas.VerticalGuide(
                                        (int)
                                            Math.Round(
                                                current.Distance / lapLength * (canvas.Width - 1)
                                            ),
                                        new Color(200, 200, 200)
                                    );
                                }
                            })
                        )
                    )
                    // Speed gets the most room, on/off channels the least
                    .Ratio(
                        channel.Name switch
                        {
                            "vCar" => 4,
                            "pBrake" or "DRS" => 1,
                            _ => 2,
                        }
                    )
            )
            .ToArray();

        // With inline images the strips are drawn as one image over this empty area
        var stripLayouts = _image.Supported ? [new Layout("Strips", new Text(""))] : strips;

        return new Layout(number).SplitRows([
            new Layout("Title", title).Size(1),
            new Layout("Legend", Legend(number)).Size(RowsAboveStrips - 1),
            .. stripLayouts,
            new Layout("Axis", DistanceAxis(lapLength)).Size(1),
        ]);
    }

    private static IEnumerable<(double X, double Y)> Points(
        TelemetryLap lap,
        Func<TelemetrySample, double> value
    ) => lap.Samples.Select(s => (s.Distance, value(s)));

    private static IRenderable ChannelLabel(string name, string unit, Color colour) =>
        new Markup($"[bold {colour.ToMarkup()}]{name}[/]\n[grey]{unit}[/]");

    /// <summary>A Name | Value | Unit table of the latest live values, like the ATLAS legend.</summary>
    private IRenderable Legend(string number)
    {
        // Live values interpolated between samples; DRS eligibility only exists in the raw data
        var hasLive = telemetry.TryGetLive(number, out var live);
        var channels = carData
            .Latest.Entries.LastOrDefault()
            ?.Cars.GetValueOrDefault(number)
            ?.Channels;
        var cells = new[]
        {
            ("vCar", hasLive ? $"{live.Speed}" : "", "kph"),
            ("nEngine", hasLive ? $"{live.Rpm}" : "", "rpm"),
            ("NGear", hasLive ? $"{live.Gear}" : "", ""),
            ("rThrottle", hasLive ? $"{live.Throttle}" : "", "%"),
            ("pBrake", hasLive && live.Brake > 0 ? "ON" : "OFF", ""),
            (
                "DRS",
                hasLive && live.DrsOpen ? "OPEN"
                : channels?.Drs is 8 ? "ELIG"
                : "SHUT",
                ""
            ),
        };

        var table = new Table().NoBorder().NoSafeBorder().HideHeaders();
        for (var i = 0; i < 3; i++)
        {
            table.AddColumn(new TableColumn("") { Width = 10, Padding = new Padding(0, 0, 1, 0) });
            table.AddColumn(
                new TableColumn("")
                {
                    Width = 6,
                    Alignment = Justify.Right,
                    Padding = new Padding(0, 0, 1, 0),
                }
            );
            table.AddColumn(new TableColumn("") { Width = 4, Padding = new Padding(0, 0, 2, 0) });
        }
        foreach (var row in cells.Chunk(3))
        {
            table.AddRow(
                row.SelectMany(cell =>
                        new IRenderable[]
                        {
                            new Text(cell.Item1, new Style(foreground: new Color(51, 153, 255))),
                            new Text(
                                cell.Item2,
                                new Style(foreground: Color.White, decoration: Decoration.Bold)
                            ),
                            new Text(cell.Item3, PitWallTheme.Dim),
                        }
                    )
                    .ToArray()
            );
        }
        return new Rows(new Markup("[grey]Name       Value  Unit[/]"), table, SpeedTraps(number));
    }

    /// <summary>
    /// The car's best speeds through the speed trap (ST), the two intermediates (I1, I2) and the
    /// finish line (FL) this session, with its rank among all cars.
    /// </summary>
    private IRenderable SpeedTraps(string number)
    {
        var speeds = timingStats.Latest.Lines.GetValueOrDefault(number)?.BestSpeeds ?? [];
        var traps = new[] { "ST", "I1", "I2", "FL" }
            .Select(trap => (Trap: trap, Speed: speeds.GetValueOrDefault(trap)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Speed?.Value))
            .Select(x =>
            {
                var rank = x.Speed!.Position;
                var colour =
                    rank == 1 ? "#cc33ff"
                    : rank <= 3 ? "#00dc00"
                    : "white";
                return $"[#3399ff]{x.Trap}[/] [bold {colour}]{x.Speed.Value}[/][grey] kph P{rank}[/]";
            })
            .ToList();
        return new SingleLine(
            "[grey]Best speeds [/] "
                + (traps.Count == 0 ? "[grey]none yet[/]" : string.Join("   ", traps))
        );
    }

    private static IRenderable DistanceAxis(double lapLength) =>
        new Markup(
            $"[grey]{new string(' ', 11)}0 m{new string(' ', 4)}distance →{new string(' ', 4)}{lapLength:0} m[/]"
        );

    private static IRenderable StatusLine() =>
        new SingleLine(
            "[white] Ready · current lap in colour, previous lap in grey · distance integrated from speed · car data ≈4 Hz[/]",
            StatusBar
        );
}
