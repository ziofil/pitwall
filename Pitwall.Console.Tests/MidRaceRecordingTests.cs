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
    public void RaceTraceShowsTheSafetyCarsAndRedFlag()
    {
        var display = fixture.Services.GetRequiredService<PitWallStrategyDisplay>();

        var bands = display.NeutralisedPeriods(56);

        // The safety car from lap 44, then the red flag on lap 45 until the standing restart
        Assert.Contains(bands, x => x.Label == "SC" && x.FromLap == 43 && x.ToLap == 44);
        Assert.Contains(bands, x => x.Label == "RED FLAG" && x.FromLap == 44 && x.ToLap == 46);
    }

    [Fact]
    public async Task RaceTraceImageMarksStopsAndNeutralisedPeriods()
    {
        var terminal = fixture.Services.GetRequiredService<NoTerminalImages>();
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = "Red Bull Racing";
        var display = fixture.Services.GetRequiredService<PitWallStrategyDisplay>();
        state.CurrentScreen = display.Screen;
        try
        {
            terminal.Supported = true;
            await display.GetContentAsync();
            Assert.NotNull(terminal.LastImage);
            Assert.NotEmpty(display.PitStopsByCar());

            var directory = Path.Join(AppContext.BaseDirectory, "renders");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(
                Path.Join(directory, "RaceTrace-Mexico.png"),
                terminal.LastImage
            );
        }
        finally
        {
            terminal.Supported = false;
        }
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

/// <summary>The Mexico recording just after it starts, before any lap has been completed.</summary>
public sealed class SampleMexicoStartFixture() : SampleSessionFixture("2026_Mexico_City_Race", 0);

public class RecordingStartTests(SampleMexicoStartFixture fixture)
    : IClassFixture<SampleMexicoStartFixture>
{
    [Fact]
    public void WithNoTeamChosenTheFirstTeamIsShown()
    {
        var state = fixture.Services.GetRequiredService<State>();
        var context = fixture.Services.GetRequiredService<PitWallContext>();
        state.SelectedTeam = null;

        Assert.Equal(context.Teams[0].Name, context.Team?.Name);
        // Kept once chosen, so the team doesn't change when the order does
        Assert.Equal(context.Teams[0].Name, state.SelectedTeam);
    }

    [Fact]
    public async Task StrategyPageWaitsForLapsInsteadOfFailing()
    {
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = "Ferrari";
        var terminal = fixture.Services.GetRequiredService<NoTerminalImages>();
        terminal.Supported = true;
        try
        {
            var display = fixture.Services.GetRequiredService<PitWallStrategyDisplay>();
            Assert.Empty(display.RaceTrace());
            var text = await fixture.RenderAsync(display);
            Assert.Contains("WAITING FOR COMPLETED LAPS", text);
        }
        finally
        {
            terminal.Supported = false;
        }
    }
}
