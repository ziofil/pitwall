using Pitwall.Data;
using SkiaSharp;
using Spectre.Console;

namespace Pitwall.Console;

/// <summary>A car's lap in progress against its best, for the qualifying delta graph.</summary>
/// <param name="Title">The car's number and name.</param>
/// <param name="Colour">The team colour, as hex.</param>
/// <param name="Trace">The lap against its best, or <c>null</c> when not on a push lap.</param>
/// <param name="Segments">Each sector's mini sectors as the car went through them on this lap.</param>
/// <param name="LiveColour">
/// The live delta's colour, as hex: purple on course for the fastest lap of all, green ahead of
/// its best, yellow behind.
/// </param>
public sealed record DeltaCar(
    string Title,
    string Colour,
    LapDeltaTrace? Trace,
    IReadOnlyList<IReadOnlyList<TimingDataPoint.Driver.StatusFlags?>> Segments,
    string LiveColour = "e6e6e6",
    double Scale = 0
);

/// <summary>
/// The qualifying delta graph: a car's time against its best lap along the lap so far, faster
/// upwards, the line coloured by mini sector as on the timing monitor (purple fastest overall,
/// green a personal best, yellow slower), with the sector lines marked. Drawn with braille
/// characters, or as an image in terminals that show them.
/// </summary>
public static class LapDeltaChart
{
    private static readonly SKColor Background = SKColor.Parse("000000");
    private static readonly SKColor Grid = SKColor.Parse("2a2a32");
    private static readonly SKColor GridText = SKColor.Parse("8a8a96");

    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(
        "Helvetica Neue",
        SKFontStyleWeight.Bold,
        SKFontStyleWidth.Normal,
        SKFontStyleSlant.Upright
    );

    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName(
        "Helvetica Neue",
        SKFontStyleWeight.Medium,
        SKFontStyleWidth.Normal,
        SKFontStyleSlant.Upright
    );

    /// <summary>The colour of the line at a point of the lap: its mini sector's.</summary>
    public static string SegmentHex(DeltaCar car, double distance)
    {
        var trace = car.Trace!;
        double[] ends = [0, .. trace.SectorEnds, trace.BestLength];
        for (var sector = 0; sector < Math.Min(3, ends.Length - 1); sector++)
        {
            if (distance > ends[sector + 1] && sector < ends.Length - 2)
                continue;
            var segments = sector < car.Segments.Count ? car.Segments[sector] : [];
            if (segments.Count == 0)
                return "e6e6e6";
            var span = Math.Max(1, ends[sector + 1] - ends[sector]);
            var index = (int)Math.Clamp(
                (distance - ends[sector]) / span * segments.Count,
                0,
                segments.Count - 1
            );
            return segments[index] switch
            {
                null or 0 => "e6e6e6",
                { } s when s.HasFlag(TimingDataPoint.Driver.StatusFlags.OverallBest) => "cc33ff",
                { } s when s.HasFlag(TimingDataPoint.Driver.StatusFlags.PersonalBest) => "00dc00",
                _ => "ffe600",
            };
        }
        return "e6e6e6";
    }

    /// <summary>
    /// The seconds either side of zero the graph shows: a round step that fits the lap's delta,
    /// at least a quarter of a second.
    /// </summary>
    public static double Range(params IEnumerable<LapDeltaTrace?> traces)
    {
        var largest = traces.Select(x => x?.Points.Max(p => Math.Abs(p.Delta)) ?? 0).DefaultIfEmpty(0).Max();
        return new[] { 0.25, 0.5, 1.0, 2.0, 3.0 }.FirstOrDefault(x => largest <= x * 0.9, 3.0);
    }

    /// <summary>Draws one car's graph with braille characters.</summary>
    public static void Draw(BrailleCanvas canvas, DeltaCar car)
    {
        if (car.Trace is not { } trace)
        {
            canvas.Label(1, Math.Max(0, (canvas.Rows / 2) - 1), "NOT ON A PUSH LAP", PitWallTheme.Dim);
            canvas.Label(1, canvas.Rows / 2, "out lap, in the pits, or no timed lap yet", PitWallTheme.Dim);
            return;
        }

        var range = car.Scale > 0 ? car.Scale : Range(trace);
        string[] labels = [$"{-range:+0.##;-0.##}", "0", $"{range:+0.##;-0.##}"];
        var gutter = labels.Max(x => x.Length) + 1;
        var plotColumns = Math.Max(4, canvas.Columns - gutter);
        var plotRows = Math.Max(2, canvas.Rows - 1);
        var (width, height) = (plotColumns * 2, plotRows * 4);
        int X(double distance) =>
            (gutter * 2) + (int)Math.Round(distance / trace.BestLength * (width - 1));
        // Faster (a negative delta) is up
        int Y(double delta) =>
            (int)Math.Round((Math.Clamp(delta, -range, range) + range) / (2 * range) * (height - 1));

        for (var x = gutter * 2; x < (gutter * 2) + width; x += 4)
            canvas.Set(x, Y(0), PitWallTheme.Grid);
        foreach (var end in trace.SectorEnds)
        {
            for (var y = 0; y < height; y += 3)
                canvas.Set(X(end), y, PitWallTheme.Grid);
        }

        var points = trace.Points;
        for (var i = 1; i < points.Count; i++)
        {
            canvas.Line(
                X(points[i - 1].Distance),
                Y(points[i - 1].Delta),
                X(points[i].Distance),
                Y(points[i].Delta),
                Color.FromHex(SegmentHex(car, points[i].Distance))
            );
        }

        canvas.Label(0, 0, labels[0].PadLeft(gutter - 1), PitWallTheme.Dim);
        canvas.Label(0, Y(0) / 4, labels[1].PadLeft(gutter - 1), PitWallTheme.Dim);
        canvas.Label(0, (height - 1) / 4, labels[2].PadLeft(gutter - 1), PitWallTheme.Dim);
        double[] starts = [0, .. trace.SectorEnds];
        for (var i = 0; i < starts.Length; i++)
            canvas.Label((X(starts[i]) / 2) + 1, canvas.Rows - 1, $"S{i + 1}", PitWallTheme.Dim);
    }

    /// <summary>Draws the cars' graphs side by side as an image.</summary>
    public static SKImage Render(IReadOnlyList<DeltaCar> cars, int width, int height)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        if (cars.Count == 0)
            return surface.Snapshot();

        var unit = Math.Max(9f, Math.Min(width / 70f, height / 16f));
        using var font = new SKFont(Regular, unit * 0.8f);
        using var titleFont = new SKFont(Bold, unit * 1.0f);
        using var bigFont = new SKFont(Bold, unit * 2.2f);
        using var gridPaint = new SKPaint { Color = Grid, StrokeWidth = 1 };
        using var gridText = new SKPaint { Color = GridText, IsAntialias = true };
        var columnWidth = width / (float)cars.Count;

        for (var c = 0; c < cars.Count; c++)
        {
            var car = cars[c];
            var left = c * columnWidth;
            using var titlePaint = new SKPaint
            {
                Color = SKColor.Parse(car.Colour),
                IsAntialias = true,
            };
            canvas.DrawText(car.Title, left + (unit * 0.6f), unit * 1.4f, titleFont, titlePaint);

            if (car.Trace is not { } trace)
            {
                canvas.DrawText(
                    "Not on a push lap: out lap, in the pits, or no timed lap yet",
                    left + (unit * 0.6f),
                    height / 2f,
                    font,
                    gridText
                );
                continue;
            }

            // The live delta, large
            var live = trace.Live;
            using var livePaint = new SKPaint
            {
                Color = SKColor.Parse(car.LiveColour),
                IsAntialias = true,
            };
            var liveText = $"{live:+0.000;-0.000}";
            canvas.DrawText(
                liveText,
                left + columnWidth - (unit * 0.8f) - bigFont.MeasureText(liveText),
                unit * 2.4f,
                bigFont,
                livePaint
            );
            canvas.DrawText(
                $"vs best {TimeSpan.FromSeconds(trace.BestSeconds):m\\:ss\\.fff}",
                left + (unit * 0.6f),
                unit * 2.6f,
                font,
                gridText
            );

            var plot = new SKRect(
                left + (unit * 3.6f),
                unit * 3.4f,
                left + columnWidth - (unit * 1.0f),
                height - (unit * 1.8f)
            );
            var range = car.Scale > 0 ? car.Scale : Range(trace);
            float X(double distance) =>
                plot.Left + (float)(distance / trace.BestLength * plot.Width);
            float Y(double delta) =>
                plot.Top
                + (float)((Math.Clamp(delta, -range, range) + range) / (2 * range) * plot.Height);

            // Zero, the range either side (faster at the top), and the sector lines
            foreach (var value in new[] { -range, 0, range })
            {
                canvas.DrawLine(plot.Left, Y(value), plot.Right, Y(value), gridPaint);
                var text = value == 0 ? "0" : $"{value:+0.##;-0.##}";
                canvas.DrawText(
                    text,
                    plot.Left - font.MeasureText(text) - (unit * 0.4f),
                    Y(value) + (unit * 0.3f),
                    font,
                    gridText
                );
            }
            canvas.DrawText("faster", plot.Left + (unit * 0.4f), plot.Top + (unit * 1.0f), font, gridText);
            canvas.DrawText("slower", plot.Left + (unit * 0.4f), plot.Bottom - (unit * 0.4f), font, gridText);
            double[] starts = [0, .. trace.SectorEnds];
            for (var i = 0; i < starts.Length; i++)
            {
                if (i > 0)
                    canvas.DrawLine(X(starts[i]), plot.Top, X(starts[i]), plot.Bottom, gridPaint);
                canvas.DrawText(
                    $"S{i + 1}",
                    X(starts[i]) + (unit * 0.3f),
                    plot.Bottom + (unit * 1.2f),
                    font,
                    gridText
                );
            }

            // The lap so far, each stretch in its mini sector's colour
            var points = trace.Points;
            for (var i = 1; i < points.Count; i++)
            {
                using var paint = new SKPaint
                {
                    Color = SKColor.Parse(SegmentHex(car, points[i].Distance)),
                    IsAntialias = true,
                    StrokeWidth = unit * 0.22f,
                    StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawLine(
                    X(points[i - 1].Distance),
                    Y(points[i - 1].Delta),
                    X(points[i].Distance),
                    Y(points[i].Delta),
                    paint
                );
            }
        }

        return surface.Snapshot();
    }
}
