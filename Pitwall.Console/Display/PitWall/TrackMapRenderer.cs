using SkiaSharp;

namespace Pitwall.Console;

/// <summary>A car to draw on the track page.</summary>
/// <param name="Progress">How far around the lap the car is, from 0 to 1.</param>
/// <param name="X">Position in the circuit's coordinates.</param>
/// <param name="Y">Position in the circuit's coordinates.</param>
/// <param name="Colour">Hex colour, e.g. <c>27F4D2</c>.</param>
/// <param name="Focus">One of the team's cars or a car right next to them.</param>
/// <param name="TeamCar">One of the selected team's cars.</param>
public sealed record TrackCar(
    string Number,
    string Tla,
    double Progress,
    int X,
    int Y,
    string Colour,
    bool Focus,
    bool TeamCar
);

/// <summary>Everything drawn on the track page.</summary>
/// <param name="Circuit">The circuit outline, in the same coordinates as the cars.</param>
/// <param name="Corners">Corner numbers and where they are.</param>
/// <param name="Messages">Lines of text shown inside the lap ring, with hex colours.</param>
public sealed record TrackScene(
    IReadOnlyList<(int X, int Y)> Circuit,
    IReadOnlyList<(int Number, float X, float Y)> Corners,
    IReadOnlyList<TrackCar> Cars,
    IReadOnlyList<(string Text, string Colour)> Messages
);

/// <summary>
/// Draws the track page as an image: the lap ring on the left and the circuit map on the right,
/// with solid anti-aliased lines and real text, for terminals that can show inline graphics.
/// </summary>
public static class TrackMapRenderer
{
    private static readonly SKColor Background = SKColor.Parse("000000");
    private static readonly SKColor RingColour = SKColor.Parse("3a3a46");
    private static readonly SKColor RingEdge = SKColor.Parse("6a6a78");
    private static readonly SKColor TrackOuter = SKColor.Parse("4a4a56");
    private static readonly SKColor TrackInner = SKColor.Parse("b4b4c0");
    private static readonly SKColor Muted = SKColor.Parse("8a8a96");
    private static readonly SKColor Bright = SKColor.Parse("f0f0f0");

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

    public static SKImage Render(TrackScene scene, int width, int height)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);

        // Text scales with the image so it stays readable at any terminal size
        var unit = Math.Max(10f, Math.Min(width, height) / 38f);

        var half = width / 2f;
        DrawRing(canvas, new SKRect(0, 0, half, height), scene, unit);
        DrawCircuit(canvas, new SKRect(half, 0, width, height), scene, unit);

        return surface.Snapshot();
    }

    private static void DrawRing(SKCanvas canvas, SKRect area, TrackScene scene, float unit)
    {
        var centre = new SKPoint(area.MidX, area.MidY);
        var radius = (Math.Min(area.Width, area.Height) / 2f) - (unit * 4.5f);
        if (radius < unit * 4)
            return;

        using var ring = new SKPaint
        {
            Color = RingColour,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = unit * 0.9f,
        };
        using var edge = new SKPaint
        {
            Color = RingEdge,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = Math.Max(1, unit * 0.08f),
        };
        canvas.DrawCircle(centre, radius, ring);
        canvas.DrawCircle(centre, radius - (unit * 0.45f), edge);
        canvas.DrawCircle(centre, radius + (unit * 0.45f), edge);

        // Every tenth of the lap is a small dot inside the ring, so any bar across the ring is a
        // car; the start/finish line is a bright line across it
        using var tenthDot = new SKPaint { Color = Muted, IsAntialias = true };
        using var startLine = new SKPaint
        {
            Color = Bright,
            IsAntialias = true,
            StrokeWidth = Math.Max(2f, unit * 0.22f),
            StrokeCap = SKStrokeCap.Round,
        };
        for (var tenth = 1; tenth < 10; tenth++)
        {
            canvas.DrawCircle(
                OnRing(centre, radius - (unit * 1.4f), tenth / 10.0 * 2 * Math.PI),
                unit * 0.14f,
                tenthDot
            );
        }
        canvas.DrawLine(
            OnRing(centre, radius - (unit * 1.4f), 0),
            OnRing(centre, radius + (unit * 1.4f), 0),
            startLine
        );
        using var smallFont = new SKFont(Regular, unit * 0.75f);
        using var mutedText = new SKPaint { Color = Muted, IsAntialias = true };
        DrawCentredText(
            canvas,
            "S/F",
            OnRing(centre, radius + (unit * 2.0f), 0),
            smallFont,
            mutedText
        );

        // Cars as bars across the ring, background cars first so highlighted ones sit on top
        using var labelFont = new SKFont(Bold, unit * 0.9f);
        var placed = new List<SKRect>();
        foreach (var car in scene.Cars.OrderBy(x => x.Focus).ThenBy(x => x.TeamCar))
        {
            var angle = car.Progress * 2 * Math.PI;
            var colour = SKColor.Parse(car.Colour);
            using var bar = new SKPaint
            {
                Color = colour,
                IsAntialias = true,
                StrokeWidth =
                    unit
                    * (
                        car.TeamCar ? 0.5f
                        : car.Focus ? 0.4f
                        : 0.28f
                    ),
                StrokeCap = SKStrokeCap.Round,
            };
            var reach = unit * (car.Focus ? 1.2f : 0.8f);
            canvas.DrawLine(
                OnRing(centre, radius - reach, angle),
                OnRing(centre, radius + reach, angle),
                bar
            );

            // Labels outside the ring, skipped if they would overlap one already placed
            var labelCentre = OnRing(centre, radius + (unit * 3.0f), angle);
            DrawLabel(canvas, car, labelCentre, labelFont, unit, placed, alwaysShow: car.Focus);
        }

        // Inside the ring: the latest race control messages, cars on track and the clocks
        using var messageFont = new SKFont(Regular, unit * 0.78f);
        var maxWidth = radius * 1.55f;
        var lineHeight = unit * 1.15f;
        var top = centre.Y - (scene.Messages.Count * lineHeight / 2f) + (lineHeight * 0.7f);
        for (var i = 0; i < scene.Messages.Count; i++)
        {
            var (text, colour) = scene.Messages[i];
            if (string.IsNullOrEmpty(text))
                continue;
            using var paint = new SKPaint { Color = SKColor.Parse(colour), IsAntialias = true };
            DrawCentredText(
                canvas,
                Fit(text, messageFont, maxWidth),
                new SKPoint(centre.X, top + (i * lineHeight)),
                messageFont,
                paint
            );
        }
    }

    private static void DrawCircuit(SKCanvas canvas, SKRect area, TrackScene scene, float unit)
    {
        var points = scene.Circuit;
        if (points.Count < 2)
            return;

        var padding = unit * 3.5f;
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var scale = Math.Min(
            (area.Width - (2 * padding)) / Math.Max(1, maxX - minX),
            (area.Height - (2 * padding)) / Math.Max(1, maxY - minY)
        );
        var offsetX = area.Left + ((area.Width - ((maxX - minX) * scale)) / 2f);
        var offsetY = area.Top + ((area.Height - ((maxY - minY) * scale)) / 2f);

        // Map y points up, image y points down
        SKPoint Transform(float x, float y) =>
            new(
                offsetX + ((x - minX) * scale),
                area.Bottom - offsetY + area.Top - ((y - minY) * scale)
            );

        using var path = new SKPath();
        path.MoveTo(Transform(points[0].X, points[0].Y));
        foreach (var (x, y) in points.Skip(1))
            path.LineTo(Transform(x, y));
        path.Close();

        // A wide dark band with a lighter centre line reads as a track surface
        using var outer = new SKPaint
        {
            Color = TrackOuter,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = unit * 0.9f,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var inner = new SKPaint
        {
            Color = TrackInner,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = Math.Max(1.5f, unit * 0.14f),
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawPath(path, outer);
        canvas.DrawPath(path, inner);

        // Start/finish line across the track at the first point
        if (points.Count > 2)
        {
            var a = Transform(points[0].X, points[0].Y);
            var b = Transform(points[1].X, points[1].Y);
            var direction = new SKPoint(b.X - a.X, b.Y - a.Y);
            var length = MathF.Max(
                0.001f,
                MathF.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y))
            );
            var normal = new SKPoint(
                -direction.Y / length * unit * 0.8f,
                direction.X / length * unit * 0.8f
            );
            using var line = new SKPaint
            {
                Color = Bright,
                IsAntialias = true,
                StrokeWidth = Math.Max(2f, unit * 0.2f),
            };
            canvas.DrawLine(a.X - normal.X, a.Y - normal.Y, a.X + normal.X, a.Y + normal.Y, line);
        }

        using var cornerFont = new SKFont(Regular, unit * 0.7f);
        using var cornerPaint = new SKPaint { Color = Muted, IsAntialias = true };
        foreach (var (number, x, y) in scene.Corners)
        {
            var point = Transform(x, y);
            DrawCentredText(
                canvas,
                $"{number}",
                new SKPoint(point.X + (unit * 1.1f), point.Y - (unit * 0.6f)),
                cornerFont,
                cornerPaint
            );
        }

        using var labelFont = new SKFont(Bold, unit * 0.85f);
        var placed = new List<SKRect>();
        foreach (var car in scene.Cars.OrderBy(x => x.Focus).ThenBy(x => x.TeamCar))
        {
            var point = Transform(car.X, car.Y);
            var radius =
                unit
                * (
                    car.TeamCar ? 0.55f
                    : car.Focus ? 0.45f
                    : 0.35f
                );
            using var fill = new SKPaint { Color = SKColor.Parse(car.Colour), IsAntialias = true };
            using var ring = new SKPaint
            {
                Color = Background,
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = Math.Max(1.5f, unit * 0.12f),
            };
            canvas.DrawCircle(point, radius, fill);
            canvas.DrawCircle(point, radius, ring);
            DrawLabel(
                canvas,
                car,
                new SKPoint(point.X + (unit * 1.9f), point.Y - (unit * 0.9f)),
                labelFont,
                unit,
                placed,
                alwaysShow: car.Focus
            );
        }
    }

    /// <summary>
    /// Draws a car's three-letter code: the team's cars as a filled tag in the team colour, other
    /// cars as coloured text. Labels that would overlap an earlier one are left out, unless they
    /// must always show.
    /// </summary>
    private static void DrawLabel(
        SKCanvas canvas,
        TrackCar car,
        SKPoint centre,
        SKFont font,
        float unit,
        List<SKRect> placed,
        bool alwaysShow
    )
    {
        var width = font.MeasureText(car.Tla);
        var bounds = new SKRect(
            centre.X - (width / 2f) - (unit * 0.3f),
            centre.Y - (font.Size * 0.75f),
            centre.X + (width / 2f) + (unit * 0.3f),
            centre.Y + (font.Size * 0.4f)
        );
        if (!alwaysShow && placed.Any(x => x.IntersectsWith(bounds)))
            return;
        placed.Add(bounds);

        var colour = SKColor.Parse(car.Colour);
        if (car.TeamCar)
        {
            using var tag = new SKPaint { Color = colour, IsAntialias = true };
            canvas.DrawRoundRect(bounds, unit * 0.2f, unit * 0.2f, tag);
            using var text = new SKPaint { Color = Background, IsAntialias = true };
            DrawCentredText(canvas, car.Tla, centre, font, text);
        }
        else
        {
            using var text = new SKPaint
            {
                Color = car.Focus ? colour : colour.WithAlpha(210),
                IsAntialias = true,
            };
            DrawCentredText(canvas, car.Tla, centre, font, text);
        }
    }

    private static void DrawCentredText(
        SKCanvas canvas,
        string text,
        SKPoint centre,
        SKFont font,
        SKPaint paint
    )
    {
        var width = font.MeasureText(text);
        canvas.DrawText(text, centre.X - (width / 2f), centre.Y + (font.Size * 0.35f), font, paint);
    }

    /// <summary>Shortens text with an ellipsis so it fits the width.</summary>
    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
            return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > maxWidth)
            text = text[..^1];
        return text + "…";
    }

    private static SKPoint OnRing(SKPoint centre, float radius, double angle) =>
        new(
            centre.X + (radius * (float)Math.Sin(angle)),
            centre.Y - (radius * (float)Math.Cos(angle))
        );
}
