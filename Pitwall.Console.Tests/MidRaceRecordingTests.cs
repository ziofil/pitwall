using Microsoft.Extensions.DependencyInjection;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>
/// The 2026 Mexico City Grand Prix, recorded from lap 29 with a safety car and a red flag: the
/// cases a full, clean race doesn't exercise.
/// </summary>
public class MidRaceRecordingTests(SampleMexicoFixture fixture) : IClassFixture<SampleMexicoFixture>
{
    [Fact]
    public void RaceTraceStartsFromTheFirstRecordedLap()
    {
        fixture.Services.GetRequiredService<State>().SelectedTeam = "Red Bull Racing";
        var traces = fixture.Services.GetRequiredService<PitWallStrategyDisplay>().RaceTrace();

        Assert.True(traces.Count >= 15, $"{traces.Count} cars traced");
        // Joined during lap 28: the first snapshot is each car's last completed lap then
        Assert.All(traces.Values, x => Assert.InRange(x[0].Lap, 26, 28));
    }

    [Fact]
    public void RedFlagDoesNotThrowTheRaceTraceOffTheChart()
    {
        var traces = fixture.Services.GetRequiredService<PitWallStrategyDisplay>().RaceTrace();

        // A 38 minute stoppage would put cars thousands of seconds off the reference pace if
        // it were counted as racing time
        var leader = fixture
            .Services.GetRequiredService<TimingDataProcessor>()
            .Latest.GetOrderedLines()
            .Keys.First();
        Assert.All(traces[leader], x => Assert.InRange(x.Delta, -300, 300));
    }

    [Fact]
    public void SmoothedGapsMatchTheFeedAfterTheRedFlag()
    {
        var timing = fixture.Services.GetRequiredService<TimingDataProcessor>();
        var smoother = fixture.Services.GetRequiredService<TimingSmoother>();

        foreach (var (number, line) in timing.Latest.Lines)
        {
            if (
                line.GapToLeaderSeconds() is not { } raw
                || raw <= 0
                || line.Retired == true
                || line.Stopped == true
            )
                continue;
            var smoothed = smoother.GapToLeader(number);
            Assert.NotNull(smoothed);
            Assert.InRange(smoothed!.Value, (double)raw - 1.5, (double)raw + 1.5);
        }
    }
}
