namespace Pitwall.Console;

/// <summary>What the radar says about rain near the circuit.</summary>
/// <param name="NearestKm">Distance to the nearest rain, or <c>null</c> if there's none on the radar.</param>
/// <param name="Bearing">Which way from the circuit the nearest rain is, e.g. <c>SW</c>.</param>
/// <param name="SpeedKmh">How fast the nearest rain is moving, if it is.</param>
/// <param name="Heading">Which way the nearest rain is moving, e.g. <c>NE</c>.</param>
/// <param name="ArrivesIn">When rain should reach the circuit at its current motion, within two hours.</param>
public sealed record RainApproach(
    bool AtCircuit,
    double? NearestKm,
    string? Bearing,
    double? SpeedKmh,
    string? Heading,
    TimeSpan? ArrivesIn
);

/// <summary>
/// A short-term rain forecast for the circuit from the radar itself: where the nearest rain is,
/// how it's moving, and whether that motion brings it over the circuit. Thunderstorms, which
/// weather models place poorly, show up here as soon as they form.
/// </summary>
public static class RadarNowcast
{
    /// <summary>Rain within this distance counts as at the circuit.</summary>
    private const double CircuitKm = 3;

    /// <summary>Radar pixels more opaque than this are rain.</summary>
    private const byte RainAlpha = 60;

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(10);
    private const int Steps = 12;

    /// <param name="latest">The newest radar frame.</param>
    /// <param name="motion">How the rain moved over the last 10 minutes, if known.</param>
    public static RainApproach Analyse(RadarPixels latest, RadarFlow? motion)
    {
        var kmPerPixel = RainRadar.RadiusKm * 2 / latest.Width;
        var (cx, cy) = (latest.Width / 2f, latest.Height / 2f);
        var circuitPixels = CircuitKm / kmPerPixel;
        bool Rain(int x, int y) =>
            x >= 0
            && y >= 0
            && x < latest.Width
            && y < latest.Height
            && latest.Rgba[(((y * latest.Width) + x) * 4) + 3] > RainAlpha;
        bool RainNear(float x, float y)
        {
            var r = (int)Math.Ceiling(circuitPixels);
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (
                    (dx * dx) + (dy * dy) <= circuitPixels * circuitPixels
                    && Rain((int)x + dx, (int)y + dy)
                )
                    return true;
            }
            return false;
        }

        // The nearest rain to the circuit
        (int X, int Y)? nearest = null;
        var nearestSquared = double.MaxValue;
        for (var y = 0; y < latest.Height; y++)
        {
            for (var x = 0; x < latest.Width; x++)
            {
                var squared = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                if (squared < nearestSquared && Rain(x, y))
                    (nearest, nearestSquared) = ((x, y), squared);
            }
        }
        if (nearest is not { } rain)
            return new RainApproach(false, null, null, null, null, null);

        double? speed = null;
        string? heading = null;
        if (motion is not null)
        {
            var (vx, vy) = motion.At(rain.X + 0.5f, rain.Y + 0.5f);
            var pixels = Math.Sqrt((vx * vx) + (vy * vy));
            if (pixels >= 0.5)
            {
                speed = pixels * kmPerPixel * (TimeSpan.FromHours(1) / Step);
                heading = Compass(vx, vy);
            }
        }

        // Follow the motion back from the circuit: the rain that will be over it in n steps is
        // where that path is now
        TimeSpan? arrives = null;
        if (motion is not null && !RainNear(cx, cy))
        {
            var (x, y) = (cx, cy);
            for (var step = 1; step <= Steps; step++)
            {
                var (vx, vy) = motion.At(x, y);
                (x, y) = (x - vx, y - vy);
                if (RainNear(x, y))
                {
                    arrives = Step * step;
                    break;
                }
            }
        }

        return new RainApproach(
            RainNear(cx, cy),
            Math.Sqrt(nearestSquared) * kmPerPixel,
            Compass(rain.X - cx, rain.Y - cy),
            speed,
            heading,
            arrives
        );
    }

    /// <summary>The compass direction of a vector in image coordinates (north up).</summary>
    private static string Compass(double dx, double dy)
    {
        var degrees = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        return new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[
            (int)Math.Round(degrees / 45) % 8
        ];
    }
}
