namespace Pitwall.Data.Tests;

public class RaceStrategyTests
{
    private static TimingDataPoint.Driver Lap(string time, bool pit = false, string? gap = null) =>
        new()
        {
            LastLapTime = new() { Value = time },
            IsPitLap = pit,
            GapToLeader = gap,
        };

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    /// <summary>Builds per-lap snapshots for one driver from lap times, with optional pit laps.</summary>
    private static Dictionary<int, Dictionary<string, TimingDataPoint.Driver>> Laps(
        string driverNumber,
        double[] times,
        int[]? pitLaps = null
    )
    {
        var result = new Dictionary<int, Dictionary<string, TimingDataPoint.Driver>>();
        for (var i = 0; i < times.Length; i++)
        {
            var lap = i + 1;
            result[lap] = new()
            {
                [driverNumber] = Lap(Format(times[i]), pitLaps?.Contains(lap) ?? false),
            };
        }
        return result;
    }

    [Fact]
    public void CleanLapsExcludeFirstLapPitLapsAndSlowLaps()
    {
        var laps = Laps("1", [95, 90, 90.5, 120, 91, 110, 90.2], pitLaps: [6]);

        var clean = RaceStrategy.GetCleanLaps(laps, "1");

        Assert.Equal([2, 3, 5, 7], clean.Select(x => x.Lap));
    }

    [Fact]
    public void PitLossIsInAndOutLapTimeMinusTheFieldOnThoseLaps()
    {
        // Rain arrives on lap 5: the field slows to 100s, and car 1 pits for intermediates.
        // In-lap 108s and out-lap 114s against the field's 100s means 22s lost.
        double[] field = [95, 90, 90, 90, 100, 100, 100, 100];
        var laps = Laps("1", [95, 90, 90, 90, 108, 114, 100, 100], pitLaps: [5, 6]);
        foreach (var other in new[] { "2", "3", "4" })
        {
            foreach (var (lap, line) in Laps(other, field))
                laps[lap][other] = line[other];
        }

        var estimate = RaceStrategy.EstimatePitLoss(laps);

        Assert.Equal(1, estimate.Samples);
        Assert.Equal(22, estimate.Seconds, precision: 3);
    }

    [Fact]
    public void PitLossFallsBackToDefaultWithoutStops()
    {
        var laps = Laps("1", [95, 90, 90, 90]);

        var estimate = RaceStrategy.EstimatePitLoss(laps);

        Assert.True(estimate.IsDefault);
        Assert.Equal(RaceStrategy.DefaultPitLossSeconds, estimate.Seconds);
    }

    [Fact]
    public void PitLossUsesTheCircuitValueBeforeAnyStops()
    {
        var laps = Laps("1", [95, 90, 90, 90]);
        var circuit = new PitLossTimes(20.8, 13.5, 14.3);

        var estimate = RaceStrategy.EstimatePitLoss(laps, circuit);

        Assert.Equal(PitLossSource.Circuit, estimate.Source);
        Assert.Equal(20.8, estimate.Seconds);
        Assert.Equal(13.5, RaceStrategy.ForTrackStatus(estimate, circuit, "4").Seconds);
        Assert.Equal(14.3, RaceStrategy.ForTrackStatus(estimate, circuit, "6").Seconds);
        Assert.Equal(20.8, RaceStrategy.ForTrackStatus(estimate, circuit, "1").Seconds);
    }

    [Fact]
    public void RejoinPredictionPlacesCarBetweenTheRightRivals()
    {
        var lines = new Dictionary<string, TimingDataPoint.Driver>
        {
            ["1"] = new() { Line = 1, GapToLeader = "LAP 20" },
            ["2"] = new() { Line = 2, GapToLeader = "+3.000" },
            ["3"] = new() { Line = 3, GapToLeader = "+10.000" },
            ["4"] = new() { Line = 4, GapToLeader = "+20.000" },
            ["5"] = new() { Line = 5, GapToLeader = "+30.000" },
        };

        var prediction = RaceStrategy.PredictRejoin(lines, "2", pitLossSeconds: 22);

        // 3 + 22 = 25s behind the leader: behind car 4 (20s), ahead of car 5 (30s)
        Assert.NotNull(prediction);
        Assert.Equal(4, prediction.Position);
        Assert.Equal("4", prediction.AheadDriverNumber);
        Assert.Equal(5, prediction.GapToAhead!.Value, precision: 3);
        Assert.Equal("5", prediction.BehindDriverNumber);
        Assert.Equal(5, prediction.GapToBehind!.Value, precision: 3);
    }

    [Fact]
    public void GapHistoryIsOrderedOldestFirst()
    {
        var laps = new Dictionary<int, Dictionary<string, TimingDataPoint.Driver>>
        {
            [1] = new()
            {
                ["1"] = new() { Line = 1, GapToLeader = "LAP 1" },
                ["2"] = new() { Line = 2, GapToLeader = "+3.0" },
            },
            [2] = new()
            {
                ["1"] = new() { Line = 1, GapToLeader = "LAP 2" },
                ["2"] = new() { Line = 2, GapToLeader = "+2.5" },
            },
            [3] = new()
            {
                ["1"] = new() { Line = 1, GapToLeader = "LAP 3" },
                ["2"] = new() { Line = 2, GapToLeader = "+2.0" },
            },
        };

        var history = RaceStrategy.GetGapHistory(laps, "1", "2", laps: 2);

        Assert.Equal([(2, 2.5), (3, 2.0)], history);
        Assert.Equal(
            -0.5,
            RaceStrategy.Slope(history.Select(x => ((double)x.Lap, x.Gap)).ToList())!.Value,
            precision: 3
        );
    }
}
