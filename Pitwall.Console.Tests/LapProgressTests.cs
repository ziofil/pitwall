namespace Pitwall.Console.Tests;

public class LapProgressTests
{
    // A 400-unit square lap, with an extra point bunched up near the start of the first side
    private static readonly List<(int x, int y)> Circuit =
    [
        (0, 0),
        (10, 0),
        (100, 0),
        (100, 100),
        (0, 100),
    ];

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(50, 0, 0.125)]
    [InlineData(55, 3, 0.1375)]
    [InlineData(100, 50, 0.375)]
    [InlineData(0, 50, 0.875)]
    public void MeasuresProgressByDistanceAlongTheOutline(double x, double y, double expected)
    {
        var distances = new[] { 0.0, 10, 100, 200, 300, 400 };
        Assert.Equal(expected, PitWallTrackMapDisplay.LapProgress(Circuit, distances, (x, y)), 6);
    }
}
