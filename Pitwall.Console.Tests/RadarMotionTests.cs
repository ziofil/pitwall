using SkiaSharp;

namespace Pitwall.Console.Tests;

public class RadarMotionTests
{
    private const int Size = 120;

    /// <summary>A frame with one square of rain at the given place.</summary>
    private static RadarPixels Rain(int left, int top)
    {
        var rgba = new byte[Size * Size * 4];
        for (var y = top; y < top + 24; y++)
        for (var x = left; x < left + 24; x++)
        {
            var i = ((y * Size) + x) * 4;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (40, 120, 220, 255);
        }
        return new RadarPixels(Size, Size, rgba);
    }

    [Fact]
    public void FindsHowTheRainMoved()
    {
        var flow = RadarMotion.Estimate(Rain(40, 40), Rain(48, 44));

        var (x, y) = flow.At(52, 52);
        Assert.InRange(x, 6, 10);
        Assert.InRange(y, 2, 6);
    }

    [Fact]
    public void DrawsTheRainPartWayAlongItsMotion()
    {
        var (from, to) = (Rain(40, 40), Rain(48, 44));
        var flow = RadarMotion.Estimate(from, to);

        using var between = RadarMotion.Between(from, to, flow, 0.5f);
        var pixels = RadarPixels.From(between);

        // Halfway, the square has moved (4, 2): solid at its middle, and the frames' own
        // squares aren't left behind as faint copies at their edges
        int Alpha(int x, int y) => pixels.Rgba[(((y * Size) + x) * 4) + 3];
        Assert.True(Alpha(44 + 12, 42 + 12) > 240);
        Assert.True(Alpha(41, 41) < 60);
        Assert.True(Alpha(70, 66) < 60);
    }
}

public class RadarNowcastTests
{
    private const int Size = 200;

    /// <summary>A frame with a square of rain centred at the given place.</summary>
    private static RadarPixels Rain(int centreX, int centreY)
    {
        var rgba = new byte[Size * Size * 4];
        for (var y = centreY - 10; y < centreY + 10; y++)
        for (var x = centreX - 10; x < centreX + 10; x++)
        {
            var i = ((y * Size) + x) * 4;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (40, 120, 220, 255);
        }
        return new RadarPixels(Size, Size, rgba);
    }

    [Fact]
    public void SeesRainHeadingForTheCircuit()
    {
        // Rain west of the circuit (at the centre), moving east 6 pixels every 10 minutes
        var before = Rain(54, 100);
        var latest = Rain(60, 100);
        var motion = RadarMotion.Estimate(before, latest);

        var rain = RadarNowcast.Analyse(latest, motion);

        // 240 km across 200 pixels: the rain's edge is 30 pixels (36 km) west, moving east at
        // about 43 km/h, so its edge reaches the circuit's 3 km in about 50 minutes
        Assert.False(rain.AtCircuit);
        Assert.Equal("W", rain.Bearing);
        Assert.InRange(rain.NearestKm!.Value, 33, 39);
        Assert.Equal("E", rain.Heading);
        Assert.InRange(rain.SpeedKmh!.Value, 35, 50);
        Assert.InRange(rain.ArrivesIn!.Value.TotalMinutes, 40, 60);
    }

    [Fact]
    public void RainMovingAwayDoesNotArrive()
    {
        var motion = RadarMotion.Estimate(Rain(60, 100), Rain(54, 100));

        var rain = RadarNowcast.Analyse(Rain(54, 100), motion);

        Assert.Equal("W", rain.Heading);
        Assert.Null(rain.ArrivesIn);
    }
}
