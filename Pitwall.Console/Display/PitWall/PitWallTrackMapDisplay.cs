using Microsoft.Extensions.Options;
using Pitwall.Console.Graphics;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// The track position page, laid out like the team track displays seen on pit walls: a lap ring
/// showing where every car is around the lap (with the latest race control messages and the
/// clocks inside), next to the circuit outline with the cars on it. The team's cars are drawn
/// in the team colour, cars off track in red, cars in the pit lane in cyan; cars out of the race
/// are left off.
/// Terminals with inline graphics get a drawn image with solid lines and real text; others get a
/// braille-character version. With position smoothing on, cars glide between the feed's roughly
/// once-a-second updates and the page redraws at ~15 fps.
/// </summary>
public sealed class PitWallTrackMapDisplay(
    PitWallComponents components,
    PitWallContext context,
    SessionInfoProcessor sessionInfo,
    PositionDataProcessor positionData,
    TrackPositionSmoother positionSmoother,
    IOptions<ConsoleOptions> options,
    TimingDataProcessor timingData,
    RaceControlMessageProcessor raceControl,
    ExtrapolatedClockProcessor extrapolatedClock,
    IDateTimeProvider dateTimeProvider,
    ITerminalImages images
) : IDisplay
{
    private readonly InlineImage _image = new(images);

    private static readonly Color TrackColour = new(170, 170, 182);
    private static readonly Color RingColour = new(110, 110, 124);
    private static readonly Color OtherCar = new(235, 235, 235);
    private static readonly Color BackgroundCar = new(150, 150, 162);

    public Screen Screen => Screen.PitWallTrackMap;

    // Redraw faster while smoothing is on so the cars animate between feed updates; when drawing
    // images, the image sets the pace so a busy machine gets fewer frames rather than lag
    public TimeSpan FrameInterval =>
        !options.Value.EnablePositionSmoothing
            ? TimeSpan.FromMilliseconds(IDisplay.DefaultFrameIntervalMs)
        : _image.Supported ? _image.FrameInterval
        : TimeSpan.FromMilliseconds(66);

    internal sealed record CarOnTrack(
        string Number,
        string Tla,
        double Progress,
        int X,
        int Y,
        Color Colour,
        bool Focus
    );

    public Task<IRenderable> GetContentAsync()
    {
        var circuit = sessionInfo.Latest.CircuitPoints;
        var entries = positionData.Latest.Position.LastOrDefault()?.Entries ?? [];

        IRenderable body;
        if (circuit.Count < 2)
        {
            body = new Markup("[yellow]No circuit map available for this session yet.[/]");
        }
        else if (entries.Count == 0)
        {
            body = new Markup(
                """
                [yellow]No car position data received yet.[/]

                Live car positions require an F1 TV subscription. Log in with [bold]pitwall login[/] to receive them.
                """
            );
        }
        else
        {
            var cars = CarsOnTrack(circuit, entries);
            if (_image.Supported)
            {
                // Drawn over the empty body after the text is drawn, see PostContentDrawAsync
                var (columns, rows) = _image.WindowCells;
                var scene = BuildScene(circuit, cars);
                _image.Update(
                    PitWallComponents.HeaderHeight,
                    0,
                    columns,
                    rows - PitWallComponents.BodyRowsOffset,
                    (width, height) => TrackMapRenderer.Render(scene, width, height)
                );
                body = new Text("");
            }
            else
            {
                body = new Layout("Track").SplitColumns(
                    new Layout("Ring", new CanvasRenderable(canvas => DrawRing(canvas, cars), 30)),
                    new Layout(
                        "Outline",
                        new CanvasRenderable(canvas => DrawOutline(canvas, circuit, cars), 30)
                    )
                );
            }
        }

        if (body is not Text)
            _image.Clear();

        return Task.FromResult(components.Page(Screen, body));
    }

    /// <summary>The cars, circuit and messages to draw, in the form the image renderer takes.</summary>
    internal TrackScene BuildScene(List<(int x, int y)> circuit, List<CarOnTrack> cars) =>
        new(
            // The session's own list, so the renderer can tell it's the same circuit each frame
            circuit,
            sessionInfo.Latest.CircuitCorners,
            cars.Select(car => new TrackCar(
                    car.Number,
                    car.Tla,
                    car.Progress,
                    car.X,
                    car.Y,
                    car.Colour.ToHex(),
                    car.Focus,
                    context.IsTeamCar(car.Number)
                ))
                .ToList(),
            InnerText(cars.Count, messages: 4)
                .Select(x =>
                    (
                        x.Text,
                        (
                            x.Style.Background != Color.Default
                                ? x.Style.Background
                                : x.Style.Foreground
                        ).ToHex()
                    )
                )
                .ToList()
        );

    /// <summary>The scene for the current data, or <c>null</c> if there's nothing to draw.</summary>
    internal TrackScene? CurrentScene()
    {
        var circuit = sessionInfo.Latest.CircuitPoints;
        var entries = positionData.Latest.Position.LastOrDefault()?.Entries ?? [];
        return circuit.Count < 2 || entries.Count == 0
            ? null
            : BuildScene(circuit, CarsOnTrack(circuit, entries));
    }

    public Task PostContentDrawAsync(bool forceDraw) => _image.DrawAsync(forceDraw);

    internal List<CarOnTrack> CarsOnTrack(
        List<(int x, int y)> circuit,
        Dictionary<string, PositionDataPoint.PositionData.Entry> entries
    )
    {
        var focus = FocusCars();
        var teamColour = PitWallTheme.TeamColour(context.Team?.Colour);
        var distances = DistancesAlong(circuit);
        var cars = new List<CarOnTrack>();
        foreach (var (number, entry) in entries)
        {
            if (entry.X is not { } rawX || entry.Y is not { } rawY)
                continue;
            (double X, double Y) position = (rawX, rawY);
            if (
                options.Value.EnablePositionSmoothing
                && positionSmoother.TryGetSmoothed(number, out var smoothed)
            )
            {
                position = smoothed;
            }
            var line = timingData.Latest.Lines.GetValueOrDefault(number);
            // Skip the safety car and medical car, which are in the position feed but aren't
            // racing, and cars out of the race, which stay in the feed where they parked
            if (line is null || line.Retired == true || line.Stopped == true)
                continue;

            var colour =
                context.IsTeamCar(number) ? teamColour
                : entry.Status == PositionDataPoint.PositionData.Entry.DriverStatus.OffTrack
                    ? PitWallTheme.Red
                : line?.InPit == true ? PitWallTheme.Cyan
                : focus.Contains(number) ? OtherCar
                : BackgroundCar;

            cars.Add(
                new CarOnTrack(
                    number,
                    context.Driver(number).Tla ?? number,
                    LapProgress(circuit, distances, position),
                    (int)Math.Round(position.X),
                    (int)Math.Round(position.Y),
                    colour,
                    focus.Contains(number)
                )
            );
        }
        return cars;
    }

    private List<(int x, int y)>? _distancesCircuit;
    private double[] _distances = [];

    /// <summary>
    /// How far along the outline each of its points is, closing the loop back to the first
    /// point; kept while the circuit stays the same.
    /// </summary>
    private double[] DistancesAlong(List<(int x, int y)> circuit)
    {
        if (ReferenceEquals(circuit, _distancesCircuit))
            return _distances;
        var distances = new double[circuit.Count + 1];
        for (var i = 1; i <= circuit.Count; i++)
        {
            var (from, to) = (circuit[i - 1], circuit[i % circuit.Count]);
            distances[i] =
                distances[i - 1]
                + Math.Sqrt(Math.Pow(to.x - from.x, 2) + Math.Pow(to.y - from.y, 2));
        }
        (_distancesCircuit, _distances) = (circuit, distances);
        return distances;
    }

    /// <summary>
    /// How far around the lap a position is, from 0 to 1: projected on to the nearest stretch of
    /// the outline and measured by distance, so cars move smoothly rather than stepping from one
    /// outline point to the next.
    /// </summary>
    internal static double LapProgress(
        List<(int x, int y)> circuit,
        double[] distances,
        (double X, double Y) position
    )
    {
        var best = double.MaxValue;
        var along = 0.0;
        for (var i = 0; i < circuit.Count; i++)
        {
            var (from, to) = (circuit[i], circuit[(i + 1) % circuit.Count]);
            var (dx, dy) = ((double)to.x - from.x, (double)to.y - from.y);
            var lengthSquared = (dx * dx) + (dy * dy);
            var t =
                lengthSquared == 0
                    ? 0
                    : Math.Clamp(
                        (((position.X - from.x) * dx) + ((position.Y - from.y) * dy))
                            / lengthSquared,
                        0,
                        1
                    );
            var (px, py) = (from.x + (t * dx) - position.X, from.y + (t * dy) - position.Y);
            var distance = (px * px) + (py * py);
            if (distance < best)
            {
                best = distance;
                along = distances[i] + (t * (distances[i + 1] - distances[i]));
            }
        }
        return along / distances[^1] % 1;
    }

    /// <summary>The team's cars and the cars directly ahead of and behind them.</summary>
    private HashSet<string> FocusCars()
    {
        var ordered = timingData.Latest.GetOrderedLines().Keys.ToList();
        var focus = new HashSet<string>();
        foreach (var number in context.Team?.DriverNumbers ?? [])
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

    private void DrawRing(BrailleCanvas canvas, List<CarOnTrack> cars)
    {
        // Braille dots are roughly square, so a circle in dot space looks round
        var centreX = canvas.Width / 2;
        var centreY = canvas.Height / 2;
        var radius = Math.Min(canvas.Width / 2 - 12, canvas.Height / 2 - 6);
        if (radius < 8)
            return;

        // The ring itself, with ticks every tenth of a lap
        // Two dots thick, so it reads as a solid line
        for (var step = 0; step < 1440; step++)
        {
            var angle = step / 1440.0 * 2 * Math.PI;
            foreach (var r in new[] { radius, radius - 1 })
            {
                canvas.Set(
                    centreX + (int)Math.Round(r * Math.Sin(angle)),
                    centreY - (int)Math.Round(r * Math.Cos(angle)),
                    RingColour
                );
            }
        }
        for (var tenth = 0; tenth < 10; tenth++)
        {
            var angle = tenth / 10.0 * 2 * Math.PI;
            var (x0, y0) = PointOnRing(centreX, centreY, radius - 2, angle);
            var (x1, y1) = PointOnRing(centreX, centreY, radius - 4, angle);
            canvas.Line(x0, y0, x1, y1, tenth == 0 ? Color.White : PitWallTheme.Grid);
        }

        // Cars as radial ticks, highlighted cars drawn last so they sit on top
        var usedLabels = new List<(int Column, int Row, int Length)>();
        foreach (var car in cars.OrderBy(x => x.Focus).ThenBy(x => context.IsTeamCar(x.Number)))
        {
            var angle = car.Progress * 2 * Math.PI;
            var (x0, y0) = PointOnRing(centreX, centreY, radius - 3, angle);
            var (x1, y1) = PointOnRing(centreX, centreY, radius + 3, angle);
            canvas.Line(x0, y0, x1, y1, car.Colour);
            canvas.Line(x0 + 1, y0, x1 + 1, y1, car.Colour);

            // Label outside the ring, nudged away from labels already placed
            var (lx, ly) = PointOnRing(centreX, centreY, radius + 9, angle);
            var column =
                (lx / 2)
                - (
                    Math.Sin(angle) < -0.3 ? 3
                    : Math.Sin(angle) > 0.3 ? 0
                    : 1
                );
            var row = ly / 4;
            var label = car.Tla;
            if (
                !usedLabels.Any(u =>
                    u.Row == row
                    && column < u.Column + u.Length + 1
                    && u.Column < column + label.Length + 1
                )
            )
            {
                usedLabels.Add((column, row, label.Length));
                canvas.Label(
                    column,
                    row,
                    label,
                    new Style(
                        foreground: car.Colour,
                        decoration: car.Focus ? Decoration.Bold : Decoration.None
                    )
                );
            }
        }

        // Inside the ring: latest race control messages, cars on track, clocks
        var maxWidth = Math.Max(10, (int)(radius * 1.3 / 2));
        // A small ring has room for the clocks only: messages that short can't be read
        var inner = InnerText(cars.Count, messages: maxWidth >= 24 ? 4 : 0);
        var firstRow = (centreY / 4) - (inner.Count / 2);
        for (var i = 0; i < inner.Count; i++)
        {
            var (text, style) = inner[i];
            if (text.Length > maxWidth)
                text = text[..(maxWidth - 1)].TrimEnd() + "…";
            canvas.Label((centreX / 2) - (text.Length / 2), firstRow + i, text, style);
        }
        canvas.Label(
            (centreX / 2) - 1,
            Math.Max(0, ((centreY - radius) / 4) - 1),
            "S/F",
            PitWallTheme.Dim
        );
    }

    private List<(string Text, Style Style)> InnerText(int carsOnTrack, int messages)
    {
        var lines = new List<(string, Style)>();
        foreach (
            var message in raceControl
                .Latest.Messages.Values.OrderByDescending(x => x.Utc)
                .Take(messages)
                .Reverse()
        )
        {
            var text = message.Message?.ToUpperInvariant() ?? "";
            lines.Add((text, PitWallCommsDisplay.MessageStyle(message)));
        }
        if (lines.Count > 0)
            lines.Add(("", Style.Plain));
        lines.Add(($"ON TRACK {carsOnTrack}", PitWallTheme.Normal));

        var localOffset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var now = dateTimeProvider.Utc.ToOffset(localOffset);
        lines.Add(($@"NOW {now:HH\:mm\:ss}", PitWallTheme.Normal));
        lines.Add(
            ($@"REM {extrapolatedClock.ExtrapolatedRemaining():h\:mm\:ss}", PitWallTheme.Normal)
        );
        return lines;
    }

    private static (int X, int Y) PointOnRing(
        int centreX,
        int centreY,
        double radius,
        double angle
    ) =>
        (
            centreX + (int)Math.Round(radius * Math.Sin(angle)),
            centreY - (int)Math.Round(radius * Math.Cos(angle))
        );

    private void DrawOutline(
        BrailleCanvas canvas,
        List<(int x, int y)> circuit,
        List<CarOnTrack> cars
    )
    {
        const int padding = 6;
        var minX = circuit.Min(p => p.x);
        var maxX = circuit.Max(p => p.x);
        var minY = circuit.Min(p => p.y);
        var maxY = circuit.Max(p => p.y);
        var scale = Math.Min(
            (canvas.Width - (2 * padding) - 8) / (double)Math.Max(1, maxX - minX),
            (canvas.Height - (2 * padding)) / (double)Math.Max(1, maxY - minY)
        );
        if (scale <= 0)
            return;

        // Centre the circuit in the canvas; map y is up, canvas y is down
        var offsetX = (canvas.Width - ((maxX - minX) * scale)) / 2;
        var offsetY = (canvas.Height - ((maxY - minY) * scale)) / 2;
        (int X, int Y) Transform(double x, double y) =>
            (
                (int)Math.Round(offsetX + ((x - minX) * scale)),
                (int)Math.Round(canvas.Height - offsetY - ((y - minY) * scale))
            );

        var previous = Transform(circuit[0].x, circuit[0].y);
        foreach (var (x, y) in circuit.Skip(1).Append(circuit[0]))
        {
            var next = Transform(x, y);
            // Two dots thick, so it reads as a solid line
            canvas.Line(previous.X, previous.Y, next.X, next.Y, TrackColour);
            canvas.Line(previous.X + 1, previous.Y, next.X + 1, next.Y, TrackColour);
            canvas.Line(previous.X, previous.Y + 1, next.X, next.Y + 1, TrackColour);
            previous = next;
        }

        foreach (var (number, x, y) in sessionInfo.Latest.CircuitCorners)
        {
            var (cx, cy) = Transform(x, y);
            canvas.Label((cx / 2) + 1, cy / 4, $"{number}", PitWallTheme.Dim);
        }

        foreach (var car in cars.OrderBy(x => x.Focus).ThenBy(x => context.IsTeamCar(x.Number)))
        {
            var (cx, cy) = Transform(car.X, car.Y);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                    canvas.Set(cx + dx, cy + dy, car.Colour);
            }
            if (car.Focus)
            {
                canvas.Label(
                    (cx / 2) + 1,
                    cy / 4,
                    car.Tla,
                    new Style(foreground: car.Colour, decoration: Decoration.Bold)
                );
            }
        }
    }
}
