using Pitwall.Data;
using SkiaSharp;

namespace Pitwall.Console;

/// <summary>A car's line on the race trace.</summary>
/// <param name="Colour">Hex colour, e.g. <c>27F4D2</c>.</param>
/// <param name="Emphasis">0 for the rest of the field, 1 for rivals next to the team, 2 for the team's cars.</param>
/// <param name="Dashed">Drawn dashed, to tell a team's second car from its first.</param>
public sealed record TraceLine(
    string Label,
    string Colour,
    int Emphasis,
    IReadOnlyList<(double Lap, double Delta)> Points,
    bool Dashed = false,
    IReadOnlyList<TraceStop>? Stops = null
);

/// <summary>A pit stop on the race trace, on the lap it was made, with the tyre fitted.</summary>
public sealed record TraceStop(double Lap, string? Compound);

/// <summary>A stretch of the race that was neutralised, e.g. a safety car, in trace laps.</summary>
public sealed record TraceBand(double FromLap, double ToLap, string Label);

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

/// <summary>
/// A car's telemetry to draw: the lap in progress, the one before it, and its live values for
/// the speedometer.
/// </summary>
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
    /// The race trace: each car's time against a reference pace through the race, ahead at the
    /// top. Every car is in its team colour, the team's cars and their rivals stand out over the
    /// rest, and safety cars, red flags and pit stops (with the tyre fitted) are marked. The
    /// vertical range is fitted to the whole field, apart from the deepest dips.
    /// </summary>
    /// <param name="totalLaps">The race distance, so the chart spans the whole race.</param>
    public static SKImage RaceTrace(
        IReadOnlyList<TraceLine> lines,
        IReadOnlyList<TraceBand> bands,
        int? totalLaps,
        int width,
        int height
    )
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        var drawn = lines.Where(x => x.Points.Count > 1).ToList();
        if (drawn.Count == 0)
            return surface.Snapshot();

        var unit = Math.Max(9f, Math.Min(width / 60f, height / 14f));
        using var font = new SKFont(Regular, unit * 0.8f);
        using var labelFont = new SKFont(Bold, unit * 0.9f);
        using var fieldLabelFont = new SKFont(Regular, unit * 0.75f);

        // The whole race, from the start to the flag, so the trace grows across it as the race
        // goes on. A recording joined part-way through has nothing before its first lap
        var firstLap = Math.Floor(drawn.Min(x => x.Points[0].Lap));
        var lastLap = drawn.Max(x => x.Points[^1].Lap);
        const double minLap = 0;
        var maxLap = Math.Max(Math.Max(totalLaps ?? 0, Math.Ceiling(lastLap)), 5);

        // Fit the range to the whole field, so every car's trace is on the chart, leaving out
        // the deepest dips (pit stops, cars stopped on track) so they don't squash the rest
        var values = drawn.SelectMany(x => x.Points.Select(p => p.Delta)).Order().ToList();
        var low = values[(int)(values.Count * 0.03)];
        var high = values[^1];
        var span = Math.Max(10, high - low);
        var maxY = high + (span * 0.12);
        var minY = low - (span * 0.12);

        var plot = new SKRect(
            unit * 3.2f,
            unit * 1.6f,
            width - (unit * 5.2f),
            height - (unit * 1.6f)
        );
        float X(double lap) => plot.Left + (float)((lap - minLap) / (maxLap - minLap) * plot.Width);
        float Y(double delta) =>
            plot.Top + (float)((1 - ((delta - minY) / (maxY - minY))) * plot.Height);

        using var gridPaint = new SKPaint { Color = Grid, StrokeWidth = 1 };
        using var gridText = new SKPaint { Color = GridText, IsAntialias = true };

        // Neutralised periods behind everything, labelled along the top; before the recording
        // started, if it joined part-way through
        if (firstLap > 1)
        {
            using var missing = new SKPaint { Color = SKColors.White.WithAlpha(10) };
            canvas.DrawRect(new SKRect(plot.Left, plot.Top, X(firstLap), plot.Bottom), missing);
            using var missingText = new SKPaint { Color = GridText, IsAntialias = true };
            const string text = "NOT RECORDED";
            if (font.MeasureText(text) < X(firstLap) - plot.Left)
            {
                canvas.DrawText(
                    text,
                    plot.Left + ((X(firstLap) - plot.Left - font.MeasureText(text)) / 2),
                    plot.MidY,
                    font,
                    missingText
                );
            }
        }
        // Each label starts after the previous one ends, so back-to-back periods stay readable
        var labelEnd = float.MinValue;
        foreach (var band in bands)
        {
            var left = Math.Max(plot.Left, X(band.FromLap));
            var right = Math.Min(plot.Right, X(band.ToLap));
            if (right <= left)
                continue;
            var colour =
                band.Label == "RED FLAG" ? SKColor.Parse("e10600") : SKColor.Parse("ffd400");
            using var fill = new SKPaint
            {
                Color = colour.WithAlpha(band.Label == "VSC" ? (byte)22 : (byte)34),
            };
            canvas.DrawRect(new SKRect(left, plot.Top, right, plot.Bottom), fill);
            using var bandText = new SKPaint { Color = colour.WithAlpha(220), IsAntialias = true };
            var labelLeft = Math.Max(left + (unit * 0.25f), labelEnd + (unit * 0.4f));
            labelEnd = labelLeft + font.MeasureText(band.Label);
            canvas.DrawText(
                band.Label,
                labelLeft,
                plot.Top - (unit * 0.35f),
                font,
                bandText
            );
        }

        // Captions for which way is ahead; they return where they end
        float Caption(string text, float baseline, bool up)
        {
            // A small triangle (the font has no arrows) then the caption
            var size = unit * 0.45f;
            var x = unit * 0.3f;
            var middle = baseline - (unit * 0.3f);
            using var triangle = new SKPath();
            triangle.MoveTo(x, middle + (up ? size / 2 : -size / 2));
            triangle.LineTo(x + size, middle + (up ? size / 2 : -size / 2));
            triangle.LineTo(x + (size / 2), middle + (up ? -size / 2 : size / 2));
            triangle.Close();
            canvas.DrawPath(triangle, gridText);
            canvas.DrawText(text, x + size + (unit * 0.3f), baseline, font, gridText);
            return x + size + (unit * 0.3f) + font.MeasureText(text);
        }
        Caption("AHEAD", plot.Top - (unit * 0.35f), up: true);
        var behindEnd = Caption("BEHIND", height - (unit * 0.35f), up: false);

        // Every lap gridded if there's room, numbered every lap, 2 or 5 as space allows
        var lapWidth = plot.Width / (maxLap - minLap);
        // A line every lap only while they're well apart; otherwise every 5 laps
        var gridStep = lapWidth >= unit * 1.2 ? 1 : 5;
        var labelStep =
            lapWidth >= unit * 1.8 ? 1
            : lapWidth * 2 >= unit * 1.8 ? 2
            : 5;
        for (var lap = Math.Ceiling(minLap / gridStep) * gridStep; lap <= maxLap; lap += gridStep)
        {
            canvas.DrawLine(X(lap), plot.Top, X(lap), plot.Bottom, gridPaint);
            var text = $"{lap}";
            if (
                lap % labelStep != 0
                || X(lap) - (font.MeasureText(text) / 2) < behindEnd + unit * 0.3f
            )
                continue;
            canvas.DrawText(
                text,
                X(lap) - (font.MeasureText(text) / 2),
                plot.Bottom + (unit * 1.15f),
                font,
                gridText
            );
        }

        // Seconds at a round step; zero (the reference car) brighter
        var step = new[] { 2.0, 5, 10, 20, 50, 100, 200, 500 }.FirstOrDefault(
            x => (maxY - minY) / x <= 8,
            1000
        );
        using var zeroPaint = new SKPaint { Color = GridText.WithAlpha(150), StrokeWidth = 1.5f };
        for (var y = Math.Ceiling(minY / step) * step; y <= maxY; y += step)
        {
            canvas.DrawLine(plot.Left, Y(y), plot.Right, Y(y), y == 0 ? zeroPaint : gridPaint);
            var text =
                Math.Abs(y) < 1e-9 ? "0"
                : y > 0 ? $"+{y:0}"
                : $"{y:0}";
            canvas.DrawText(
                text,
                plot.Left - font.MeasureText(text) - (unit * 0.4f),
                Y(y) + (unit * 0.3f),
                font,
                gridText
            );
        }

        // The field first, then rivals, then the team's cars on top, clipped to the chart
        canvas.Save();
        canvas.ClipRect(plot);
        foreach (var line in drawn.OrderBy(x => x.Emphasis))
        {
            // Broken where laps are missing, e.g. under a red flag, rather than bridged
            using var path = new SKPath();
            path.MoveTo(X(line.Points[0].Lap), Y(line.Points[0].Delta));
            for (var i = 1; i < line.Points.Count; i++)
            {
                var (lap, delta) = line.Points[i];
                if (lap - line.Points[i - 1].Lap > 1.01)
                    path.MoveTo(X(lap), Y(delta));
                else
                    path.LineTo(X(lap), Y(delta));
            }

            var colour = SKColor.Parse(line.Colour);
            var strokeWidth = line.Emphasis switch
            {
                2 => unit * 0.24f,
                1 => unit * 0.15f,
                _ => Math.Max(1f, unit * 0.09f),
            };
            if (line.Emphasis == 2)
            {
                // A soft glow under the team's cars
                using var glow = new SKPaint
                {
                    Color = colour.WithAlpha(60),
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = strokeWidth * 3,
                    StrokeJoin = SKStrokeJoin.Round,
                    StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawPath(path, glow);
            }
            using var paint = new SKPaint
            {
                // The field stays readable but steps back behind the highlighted cars
                Color = line.Emphasis == 0 ? colour.WithAlpha(95) : colour,
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                PathEffect = line.Dashed
                    ? SKPathEffect.CreateDash([strokeWidth * 3, strokeWidth * 2.5f], 0)
                    : null,
            };
            canvas.DrawPath(path, paint);
        }

        // Pit stops of the highlighted cars, filled with the colour of the tyre fitted
        foreach (var line in drawn.Where(x => x.Emphasis > 0))
        {
            foreach (var stop in line.Stops ?? [])
            {
                var (lap, delta) = line.Points.MinBy(x => Math.Abs(x.Lap - stop.Lap));
                using var tyre = new SKPaint
                {
                    Color = SKColor.Parse(CompoundColour(stop.Compound)),
                    IsAntialias = true,
                };
                using var ring = new SKPaint
                {
                    Color = Background,
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = unit * 0.12f,
                };
                var centre = new SKPoint(X(lap), Y(delta));
                canvas.DrawCircle(centre, unit * 0.38f, tyre);
                canvas.DrawCircle(centre, unit * 0.38f, ring);
            }
        }
        canvas.Restore();

        // Labels at the line ends, spaced so they don't overlap: placed top to bottom at least a
        // line apart, then moved up together if they'd run off the bottom. The whole field is
        // labelled when there's room, otherwise only the highlighted cars
        var spacing = unit * 0.95f;
        // Cars still running: their lines reach the latest lap
        var labelled = drawn.Where(x => x.Points[^1].Lap >= lastLap - 1).ToList();
        // Labels may use the plot's height and the caption space above it, never the lap numbers
        var labelTop = unit * 0.9f;
        var labelBottom = plot.Bottom + (unit * 0.3f);
        if (labelled.Count * spacing > labelBottom - labelTop)
            labelled = [.. labelled.Where(x => x.Emphasis > 0)];
        var labels = labelled
            .Select(x =>
                (
                    Line: x,
                    Y: Math.Clamp(Y(x.Points[^1].Delta), plot.Top, plot.Bottom) + (unit * 0.3f)
                )
            )
            .OrderBy(x => x.Y)
            .ToList();
        for (var i = 1; i < labels.Count; i++)
        {
            if (labels[i].Y < labels[i - 1].Y + spacing)
                labels[i] = (labels[i].Line, labels[i - 1].Y + spacing);
        }
        // From the bottom up, keep each label above the one below it, so nothing runs off
        for (var i = labels.Count - 1; i >= 0; i--)
        {
            var limit = i == labels.Count - 1 ? labelBottom : labels[i + 1].Y - spacing;
            if (labels[i].Y > limit)
                labels[i] = (labels[i].Line, limit);
        }
        // Too many highlighted cars for the height: drop the ones pushed off the top
        labels = [.. labels.Where(x => x.Y >= labelTop)];
        foreach (var (line, y) in labels)
        {
            using var paint = new SKPaint
            {
                Color = SKColor
                    .Parse(line.Colour)
                    .WithAlpha(line.Emphasis == 0 ? (byte)150 : (byte)255),
                IsAntialias = true,
            };
            // Just after the line ends, which is the right edge only by the end of the race
            canvas.DrawText(
                line.Label,
                Math.Min(X(lastLap), plot.Right) + (unit * 0.4f),
                y,
                line.Emphasis == 0 ? fieldLabelFont : labelFont,
                paint
            );
        }

        return surface.Snapshot();
    }

    private static string CompoundColour(string? compound) =>
        compound switch
        {
            "SOFT" => "ff2a2a",
            "MEDIUM" => "ffd400",
            "HARD" => "f0f0f0",
            "INTERMEDIATE" => "2fbf2f",
            "WET" => "2f7fff",
            _ => "9a9aa2",
        };

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
