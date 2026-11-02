using Pitwall.Data;
using SkiaSharp;

namespace Pitwall.Console;

/// <summary>A car's line on the race trace.</summary>
/// <param name="Colour">Hex colour, e.g. <c>27F4D2</c>.</param>
/// <param name="Emphasis">0 for the rest of the field, 1 for rivals next to the team, 2 for the team's cars.</param>
public sealed record TraceLine(
    string Label,
    string Colour,
    int Emphasis,
    IReadOnlyList<(double Lap, double Delta)> Points
);

/// <summary>One telemetry channel strip.</summary>
public sealed record TelemetryChannel(
    string Name,
    string Unit,
    string Colour,
    Func<TelemetrySample, double> Value,
    double Min,
    double Max,
    int Weight
);

/// <summary>A car's telemetry to draw: the lap in progress and the one before it.</summary>
public sealed record TelemetryCar(TelemetryLap? Current, TelemetryLap? Previous);

/// <summary>
/// Draws the strategy page's race trace and the telemetry page's traces as images, for
/// terminals with inline graphics: solid anti-aliased lines instead of braille dots.
/// </summary>
public static class ChartRenderers
{
    private static readonly SKColor Background = SKColor.Parse("000000");
    private static readonly SKColor Grid = SKColor.Parse("2a2a34");
    private static readonly SKColor GridText = SKColor.Parse("7a7a88");
    private static readonly SKColor FieldLine = SKColor.Parse("3c3c48");
    private static readonly SKColor PreviousLap = SKColor.Parse("5a5a66");
    private static readonly SKColor Cursor = SKColor.Parse("d0d0d8");

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

    /// <summary>
    /// The race trace: each car's time against a reference pace by lap. The vertical range is
    /// fitted to the highlighted cars so the interesting part of the field fills the chart.
    /// </summary>
    public static SKImage RaceTrace(IReadOnlyList<TraceLine> lines, int width, int height)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        if (lines.Count == 0)
            return surface.Snapshot();

        var unit = Math.Max(9f, Math.Min(width / 60f, height / 14f));
        using var font = new SKFont(Regular, unit * 0.85f);
        using var labelFont = new SKFont(Bold, unit);

        var maxLap = Math.Max(5, lines.Max(x => x.Points.Count == 0 ? 0 : x.Points[^1].Lap));
        var focusValues = lines
            .Where(x => x.Emphasis > 0)
            .SelectMany(x => x.Points.Select(p => p.Delta))
            .DefaultIfEmpty(0)
            .ToList();
        var maxY = Math.Max(5, (Math.Ceiling(focusValues.Max() / 10) * 10) + 5);
        var minY = (Math.Floor(focusValues.Min() / 10) * 10) - 5;

        var plot = new SKRect(
            unit * 3f,
            unit * 0.6f,
            width - (unit * 4.5f),
            height - (unit * 1.6f)
        );
        float X(double lap) => plot.Left + (float)(lap / maxLap * plot.Width);
        float Y(double delta) =>
            plot.Top
            + (float)((1 - ((Math.Clamp(delta, minY, maxY) - minY) / (maxY - minY))) * plot.Height);

        // Grid every 10 seconds and every 5 laps, with their values
        using var gridPaint = new SKPaint
        {
            Color = Grid,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        using var gridText = new SKPaint { Color = GridText, IsAntialias = true };
        for (var y = Math.Ceiling(minY / 10) * 10; y <= maxY; y += 10)
        {
            canvas.DrawLine(plot.Left, Y(y), plot.Right, Y(y), gridPaint);
            var text = $"{y:0}";
            canvas.DrawText(
                text,
                plot.Left - font.MeasureText(text) - (unit * 0.4f),
                Y(y) + (unit * 0.3f),
                font,
                gridText
            );
        }
        for (var lap = 5; lap <= maxLap; lap += 5)
        {
            canvas.DrawLine(X(lap), plot.Top, X(lap), plot.Bottom, gridPaint);
            var text = $"{lap}";
            canvas.DrawText(
                text,
                X(lap) - (font.MeasureText(text) / 2),
                plot.Bottom + (unit * 1.2f),
                font,
                gridText
            );
        }

        // The field first, then rivals, then the team's cars on top
        foreach (var line in lines.OrderBy(x => x.Emphasis))
        {
            if (line.Points.Count < 2)
                continue;
            using var path = new SKPath();
            path.MoveTo(X(line.Points[0].Lap), Y(line.Points[0].Delta));
            foreach (var (lap, delta) in line.Points.Skip(1))
                path.LineTo(X(lap), Y(delta));
            using var paint = new SKPaint
            {
                Color = line.Emphasis == 0 ? FieldLine : SKColor.Parse(line.Colour),
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = line.Emphasis switch
                {
                    2 => unit * 0.28f,
                    1 => unit * 0.18f,
                    _ => Math.Max(1f, unit * 0.1f),
                },
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.DrawPath(path, paint);
        }

        // Labels at the end of the highlighted lines, spaced so they don't overlap: placed top to
        // bottom at least a line apart, then moved up together if they'd run off the bottom
        var spacing = unit * 1.1f;
        var labels = lines
            .Where(x => x.Emphasis > 0 && x.Points.Count > 0)
            .Select(x => (Line: x, Y: Y(x.Points[^1].Delta) + (unit * 0.35f)))
            .OrderBy(x => x.Y)
            .ToList();
        for (var i = 1; i < labels.Count; i++)
        {
            if (labels[i].Y < labels[i - 1].Y + spacing)
                labels[i] = (labels[i].Line, labels[i - 1].Y + spacing);
        }
        var overflow = labels.Count == 0 ? 0 : labels[^1].Y - (height - (unit * 0.3f));
        foreach (var (line, y) in labels)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(line.Colour),
                IsAntialias = true,
            };
            canvas.DrawText(
                line.Label,
                plot.Right + (unit * 0.4f),
                Math.Max(unit, y - Math.Max(0, overflow)),
                labelFont,
                paint
            );
        }

        return surface.Snapshot();
    }

    /// <summary>
    /// Telemetry traces for each car side by side: one strip per channel against lap distance,
    /// the previous lap in grey under the lap in progress, and a cursor at the car's position.
    /// </summary>
    /// <param name="labelWidth">Width of the channel label column, in pixels.</param>
    public static SKImage Telemetry(
        IReadOnlyList<TelemetryCar> cars,
        IReadOnlyList<TelemetryChannel> channels,
        int width,
        int height,
        float labelWidth
    )
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        if (cars.Count == 0)
            return surface.Snapshot();

        var columnWidth = width / (float)cars.Count;
        var totalWeight = channels.Sum(x => x.Weight);
        var unit = Math.Max(9f, Math.Min(labelWidth / 6f, height / 30f));
        using var nameFont = new SKFont(Bold, unit);
        using var unitFont = new SKFont(Regular, unit * 0.85f);
        using var gridPaint = new SKPaint { Color = Grid, StrokeWidth = 1 };
        using var unitPaint = new SKPaint { Color = GridText, IsAntialias = true };

        for (var c = 0; c < cars.Count; c++)
        {
            var car = cars[c];
            var left = c * columnWidth;
            var plotLeft = left + labelWidth;
            var plotRight = left + columnWidth - (unit * 0.8f);

            // A full lap: the previous lap's length if there is one
            var lapLength = Math.Max(
                100,
                Math.Max(car.Previous?.Distance ?? 0, car.Current?.Distance ?? 0)
            );

            var top = 0f;
            foreach (var channel in channels)
            {
                var stripHeight = height * channel.Weight / (float)totalWeight;
                var strip = new SKRect(
                    plotLeft,
                    top + (unit * 0.35f),
                    plotRight,
                    top + stripHeight - (unit * 0.35f)
                );
                float X(double distance) =>
                    strip.Left + (float)(distance / lapLength * strip.Width);
                float Y(double value) =>
                    strip.Top
                    + (float)(
                        (
                            1
                            - (
                                (Math.Clamp(value, channel.Min, channel.Max) - channel.Min)
                                / (channel.Max - channel.Min)
                            )
                        ) * strip.Height
                    );

                var colour = SKColor.Parse(channel.Colour);
                using var namePaint = new SKPaint { Color = colour, IsAntialias = true };
                canvas.DrawText(
                    channel.Name,
                    left + (unit * 0.2f),
                    top + (unit * 1.2f),
                    nameFont,
                    namePaint
                );
                if (stripHeight > unit * 2.6f)
                    canvas.DrawText(
                        channel.Unit,
                        left + (unit * 0.2f),
                        top + (unit * 2.3f),
                        unitFont,
                        unitPaint
                    );
                canvas.DrawLine(strip.Left, strip.Bottom, strip.Right, strip.Bottom, gridPaint);

                DrawTrace(canvas, car.Previous, channel, X, Y, PreviousLap, unit * 0.12f);
                DrawTrace(canvas, car.Current, channel, X, Y, colour, unit * 0.16f);

                if (car.Current is { Samples.Count: > 0 } current)
                {
                    using var cursor = new SKPaint
                    {
                        Color = Cursor,
                        StrokeWidth = Math.Max(1f, unit * 0.1f),
                    };
                    var x = X(current.Distance);
                    canvas.DrawLine(x, strip.Top, x, strip.Bottom, cursor);
                }

                top += stripHeight;
            }
        }

        return surface.Snapshot();
    }

    private static void DrawTrace(
        SKCanvas canvas,
        TelemetryLap? lap,
        TelemetryChannel channel,
        Func<double, float> x,
        Func<double, float> y,
        SKColor colour,
        float strokeWidth
    )
    {
        if (lap is null || lap.Samples.Count < 2)
            return;
        using var path = new SKPath();
        path.MoveTo(x(lap.Samples[0].Distance), y(channel.Value(lap.Samples[0])));
        foreach (var sample in lap.Samples.Skip(1))
            path.LineTo(x(sample.Distance), y(channel.Value(sample)));
        using var paint = new SKPaint
        {
            Color = colour,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = Math.Max(1f, strokeWidth),
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawPath(path, paint);
    }
}
