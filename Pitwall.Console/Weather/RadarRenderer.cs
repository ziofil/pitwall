using SkiaSharp;

namespace Pitwall.Console;

/// <summary>What to draw on the radar image.</summary>
/// <param name="Map">The dark base map, or <c>null</c> while it loads.</param>
/// <param name="Radar">The radar frame shown, or <c>null</c> for none.</param>
/// <param name="Frame">Which frame of the loop is shown, from 1, and how many there are.</param>
/// <param name="Message">Shown in the middle instead of the radar, e.g. while loading.</param>
/// <param name="Note">A small warning under the label.</param>
public sealed record RadarScene(
    SKImage? Map,
    SKImage? Radar,
    string Label,
    int Frame,
    int Frames,
    string? Message,
    string? Note
);

/// <summary>
/// Draws the rain radar: the map and radar fitted to a square, the circuit in the middle with
/// range rings, which way is north, the frame time and the data credits.
/// </summary>
public static class RadarRenderer
{
    private static readonly SKColor Background = SKColor.Parse("0b0b0e");
    private static readonly SKColor Ring = SKColor.Parse("8a8a96");
    private static readonly SKColor Text = SKColor.Parse("e8e8ec");
    private static readonly SKColor Dim = SKColor.Parse("8a8a96");
    private static readonly SKColor Circuit = SKColor.Parse("ff2a2a");

    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(
        "Helvetica Neue",
        SKFontStyleWeight.Bold,
        SKFontStyleWidth.Normal,
        SKFontStyleSlant.Upright
    );

    private static readonly SKTypeface Medium = SKTypeface.FromFamilyName(
        "Helvetica Neue",
        SKFontStyleWeight.Medium,
        SKFontStyleWidth.Normal,
        SKFontStyleSlant.Upright
    );

    public static SKImage Render(RadarScene scene, int width, int height)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        var canvas = surface.Canvas;
        canvas.Clear(Background);

        var side = Math.Min(width, height);
        var square = SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side);
        var unit = Math.Max(10f, side / 40f);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        if (scene.Map is not null)
            canvas.DrawImage(scene.Map, square, sampling);
        if (scene.Radar is not null)
        {
            using var radarPaint = new SKPaint { Color = SKColors.White.WithAlpha(215) };
            // The radar is coarse, so it's scaled up with a smooth cubic filter
            canvas.DrawImage(
                scene.Radar,
                square,
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                radarPaint
            );
        }

        // Range rings around the circuit
        var centre = new SKPoint(square.MidX, square.MidY);
        var pixelsPerKm = side / 2f / (float)RainRadar.RadiusKm;
        using var ringPaint = new SKPaint
        {
            Color = Ring.WithAlpha(150),
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = Math.Max(1f, unit * 0.08f),
            PathEffect = SKPathEffect.CreateDash([unit * 0.4f, unit * 0.4f], 0),
        };
        using var ringFont = new SKFont(Medium, unit * 0.8f);
        using var dimPaint = new SKPaint { Color = Dim, IsAntialias = true };
        foreach (var km in new[] { 25, 50, 100 })
        {
            var radius = km * pixelsPerKm;
            canvas.DrawCircle(centre, radius, ringPaint);
            canvas.DrawText(
                $"{km} km",
                centre.X + (radius * 0.71f) + (unit * 0.2f),
                centre.Y - (radius * 0.71f) - (unit * 0.2f),
                ringFont,
                dimPaint
            );
        }

        using var circuitPaint = new SKPaint
        {
            Color = Circuit,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = unit * 0.25f,
        };
        canvas.DrawCircle(centre, unit * 0.6f, circuitPaint);

        // North, top right
        using var textPaint = new SKPaint { Color = Text, IsAntialias = true };
        using var labelFont = new SKFont(Bold, unit * 1.1f);
        var north = new SKPoint(square.Right - (unit * 1.6f), square.Top + (unit * 1.2f));
        using var arrow = new SKPath();
        arrow.MoveTo(north.X, north.Y);
        arrow.LineTo(north.X - (unit * 0.5f), north.Y + (unit * 1.3f));
        arrow.LineTo(north.X + (unit * 0.5f), north.Y + (unit * 1.3f));
        arrow.Close();
        canvas.DrawPath(arrow, textPaint);
        canvas.DrawText(
            "N",
            north.X - (labelFont.MeasureText("N") / 2),
            north.Y + (unit * 2.6f),
            labelFont,
            textPaint
        );

        // Frame time and loop position, top left
        canvas.DrawText(
            scene.Label,
            square.Left + unit,
            square.Top + (unit * 1.8f),
            labelFont,
            textPaint
        );
        if (scene.Frames > 1)
        {
            var dot = unit * 0.25f;
            for (var i = 0; i < scene.Frames; i++)
            {
                using var dotPaint = new SKPaint
                {
                    Color = i < scene.Frame ? Text : Dim.WithAlpha(120),
                    IsAntialias = true,
                };
                canvas.DrawCircle(
                    square.Left + unit + dot + (i * dot * 3),
                    square.Top + (unit * 2.7f),
                    dot,
                    dotPaint
                );
            }
        }
        if (scene.Note is not null)
            canvas.DrawText(
                scene.Note,
                square.Left + unit,
                square.Top + (unit * 4f),
                ringFont,
                dimPaint
            );

        if (scene.Message is not null)
        {
            canvas.DrawText(
                scene.Message,
                centre.X - (labelFont.MeasureText(scene.Message) / 2),
                centre.Y + (unit * 2.5f),
                labelFont,
                textPaint
            );
        }

        const string credits = "Radar RainViewer · Map © CARTO © OpenStreetMap";
        using var creditFont = new SKFont(Medium, unit * 0.7f);
        canvas.DrawText(
            credits,
            square.Right - creditFont.MeasureText(credits) - (unit * 0.5f),
            square.Bottom - (unit * 0.5f),
            creditFont,
            dimPaint
        );

        return surface.Snapshot();
    }
}
