using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// The track position page, laid out like the team track displays seen on pit walls: a lap ring
/// showing where every car is around the lap (with the latest race control messages and the
/// clocks inside), next to the circuit outline with the cars on it. The team's cars are drawn
/// in the team colour, cars off track or stopped in red, cars in the pit lane in cyan.
/// Drawn with braille characters so it works in any terminal. With position smoothing on, cars
/// glide between the feed's roughly once-a-second updates and the page redraws at ~15 fps.
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
    IDateTimeProvider dateTimeProvider
) : IDisplay
{
    private static readonly Color TrackColour = new(150, 150, 160);
    private static readonly Color OtherCar = new(235, 235, 235);
    private static readonly Color BackgroundCar = new(120, 120, 130);

    public Screen Screen => Screen.PitWallTrackMap;

    // Redraw faster while smoothing is on so the cars animate between feed updates
    public TimeSpan FrameInterval =>
        options.Value.EnablePositionSmoothing
            ? TimeSpan.FromMilliseconds(66)
            : TimeSpan.FromMilliseconds(IDisplay.DefaultFrameIntervalMs);

    private sealed record CarOnTrack(
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
            body = new Layout("Track").SplitColumns(
                new Layout("Ring", new CanvasRenderable(canvas => DrawRing(canvas, cars), 30)),
                new Layout("Outline", new CanvasRenderable(canvas => DrawOutline(canvas, circuit, cars), 30))
            );
        }

        return Task.FromResult(components.Page(Screen, body));
    }

    private List<CarOnTrack> CarsOnTrack(
        List<(int x, int y)> circuit,
        Dictionary<string, PositionDataPoint.PositionData.Entry> entries
    )
    {
        var focus = FocusCars();
        var teamColour = PitWallTheme.TeamColour(context.Team?.Colour);
        var cars = new List<CarOnTrack>();
        foreach (var (number, entry) in entries)
        {
            if (entry.X is not { } x || entry.Y is not { } y)
                continue;
            if (options.Value.EnablePositionSmoothing && positionSmoother.TryGetSmoothed(number, out var smoothed))
            {
                (x, y) = ((int)Math.Round(smoothed.x), (int)Math.Round(smoothed.y));
            }
            var line = timingData.Latest.Lines.GetValueOrDefault(number);
            if (line?.Retired == true)
                continue;

            // Progress around the lap is the index of the nearest point on the circuit outline
            var nearest = 0;
            var nearestDistance = long.MaxValue;
            for (var i = 0; i < circuit.Count; i++)
            {
                long dx = circuit[i].x - x;
                long dy = circuit[i].y - y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = i;
                }
            }

            var colour =
                context.IsTeamCar(number) ? teamColour
                : entry.Status == PositionDataPoint.PositionData.Entry.DriverStatus.OffTrack
                || line?.Stopped == true
                    ? PitWallTheme.Red
                : line?.InPit == true ? PitWallTheme.Cyan
                : focus.Contains(number) ? OtherCar
                : BackgroundCar;

            cars.Add(
                new CarOnTrack(
                    number,
                    context.Driver(number).Tla ?? number,
                    nearest / (double)circuit.Count,
                    x,
                    y,
                    colour,
                    focus.Contains(number)
                )
            );
        }
        return cars;
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
        for (var step = 0; step < 720; step++)
        {
            var angle = step / 720.0 * 2 * Math.PI;
            canvas.Set(
                centreX + (int)Math.Round(radius * Math.Sin(angle)),
                centreY - (int)Math.Round(radius * Math.Cos(angle)),
                PitWallTheme.Grid
            );
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

            // Label outside the ring, nudged away from labels already placed
            var (lx, ly) = PointOnRing(centreX, centreY, radius + 9, angle);
            var column = (lx / 2) - (Math.Sin(angle) < -0.3 ? 3 : Math.Sin(angle) > 0.3 ? 0 : 1);
            var row = ly / 4;
            var label = car.Tla;
            if (!usedLabels.Any(u => u.Row == row && column < u.Column + u.Length + 1 && u.Column < column + label.Length + 1))
            {
                usedLabels.Add((column, row, label.Length));
                canvas.Label(
                    column,
                    row,
                    label,
                    new Style(foreground: car.Colour, decoration: car.Focus ? Decoration.Bold : Decoration.None)
                );
            }
        }

        // Inside the ring: latest race control messages, cars on track, clocks
        var inner = InnerText(cars.Count);
        var firstRow = (centreY / 4) - (inner.Count / 2);
        var maxWidth = Math.Max(10, (int)(radius * 1.3 / 2));
        for (var i = 0; i < inner.Count; i++)
        {
            var (text, style) = inner[i];
            if (text.Length > maxWidth)
                text = text[..maxWidth];
            canvas.Label((centreX / 2) - (text.Length / 2), firstRow + i, text, style);
        }
        canvas.Label((centreX / 2) - 1, Math.Max(0, ((centreY - radius) / 4) - 1), "S/F", PitWallTheme.Dim);
    }

    private List<(string Text, Style Style)> InnerText(int carsOnTrack)
    {
        var lines = new List<(string, Style)>();
        foreach (var message in raceControl.Latest.Messages.Values.OrderByDescending(x => x.Utc).Take(4).Reverse())
        {
            var text = message.Message?.ToUpperInvariant() ?? "";
            lines.Add((text, PitWallCommsDisplay.MessageStyle(message)));
        }
        lines.Add(("", Style.Plain));
        lines.Add(($"ON TRACK: {carsOnTrack}", PitWallTheme.Normal));

        var localOffset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var now = dateTimeProvider.Utc.ToOffset(localOffset);
        lines.Add(($@"NOW: {now:HH\:mm\:ss}  END: {extrapolatedClock.ExtrapolatedRemaining():h\:mm\:ss}", PitWallTheme.Normal));
        return lines;
    }

    private static (int X, int Y) PointOnRing(int centreX, int centreY, double radius, double angle) =>
        (centreX + (int)Math.Round(radius * Math.Sin(angle)), centreY - (int)Math.Round(radius * Math.Cos(angle)));

    private void DrawOutline(BrailleCanvas canvas, List<(int x, int y)> circuit, List<CarOnTrack> cars)
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
            ((int)Math.Round(offsetX + ((x - minX) * scale)), (int)Math.Round(canvas.Height - offsetY - ((y - minY) * scale)));

        var previous = Transform(circuit[0].x, circuit[0].y);
        foreach (var point in circuit.Skip(1).Append(circuit[0]))
        {
            var next = Transform(point.x, point.y);
            canvas.Line(previous.X, previous.Y, next.X, next.Y, TrackColour);
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
                canvas.Label((cx / 2) + 1, cy / 4, car.Tla, new Style(foreground: car.Colour, decoration: Decoration.Bold));
            }
        }
    }
}
