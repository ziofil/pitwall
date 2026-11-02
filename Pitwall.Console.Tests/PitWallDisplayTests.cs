using Microsoft.Extensions.DependencyInjection;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>
/// Renders each pit wall page against the replayed 2024 Silverstone race, for Mercedes
/// (Hamilton and Russell), and checks the key information is shown.
/// </summary>
public class PitWallDisplayTests(SampleRaceFixture fixture) : IClassFixture<SampleRaceFixture>
{
    private async Task<string> RenderAsync<T>(string team = "Mercedes", int width = 160, int height = 45)
        where T : IDisplay
    {
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = team;
        var display = fixture.Services.GetRequiredService<T>();
        state.CurrentScreen = display.Screen;
        state.CursorOffset = 0;
        return await fixture.RenderAsync(display, width, height);
    }

    [Fact]
    public async Task TeamListShowsEveryTeam()
    {
        var text = await RenderAsync<SelectTeamDisplay>();

        Assert.Contains("MERCEDES", text);
        Assert.Contains("MCLAREN", text);
        Assert.Contains("FERRARI", text);
        Assert.Equal(10, fixture.Services.GetRequiredService<PitWallContext>().Teams.Count);
    }

    [Fact]
    public async Task TimingPageLooksLikeTheTimingMonitor()
    {
        var text = await RenderAsync<PitWallTimingDisplay>();

        Assert.Contains("POS", text);
        Assert.Contains("L. HAMILTON", text);
        Assert.Contains("G. RUSSELL", text);
        Assert.Contains("LAST LAP", text);
        Assert.Contains($"LAP {SampleRaceFixture.ReplayToLap + 1}/52", text);
    }

    [Fact]
    public async Task StrategyPageShowsBothCarsAndPitPrediction()
    {
        var text = await RenderAsync<PitWallStrategyDisplay>();

        Assert.Contains("RACE TRACE", text);
        Assert.Contains("HAM", text);
        Assert.Contains("RUS", text);
        Assert.Contains("PIT NOW", text);
        Assert.Contains("UNDERCUT", text);
        Assert.Contains("PIT LOSS", text);
    }

    [Fact]
    public async Task TelemetryPageShowsChannelsForBothCars()
    {
        var text = await RenderAsync<PitWallTelemetryDisplay>();

        Assert.Contains("Waveform · 44 HAM", text);
        Assert.Contains("Waveform · 63 RUS", text);
        Assert.Contains("vCar", text);
        Assert.Contains("nEngine", text);
    }

    [Fact]
    public async Task TrackMapShowsRingAndCars()
    {
        var text = await RenderAsync<PitWallTrackMapDisplay>();

        if (!fixture.HasCircuitMap)
        {
            Assert.Contains("No circuit map available", text);
            return;
        }
        Assert.Contains("ON TRACK", text);
        Assert.Contains("HAM", text);
        Assert.Contains("S/F", text);
    }

    [Fact]
    public async Task CommsPageShowsRaceControlAndTeamRadio()
    {
        var text = await RenderAsync<PitWallCommsDisplay>();

        Assert.Contains("RACE CONTROL", text);
        Assert.Contains("TEAM RADIO", text);
        Assert.Contains("44 HAM", text);
    }

    [Fact]
    public async Task PagesRenderInASmallTerminal()
    {
        // Mostly checks nothing throws when the space is tight
        foreach (var render in new Func<Task<string>>[]
        {
            () => RenderAsync<PitWallTimingDisplay>(width: 80, height: 24),
            () => RenderAsync<PitWallStrategyDisplay>(width: 80, height: 24),
            () => RenderAsync<PitWallTelemetryDisplay>(width: 80, height: 24),
            () => RenderAsync<PitWallTrackMapDisplay>(width: 80, height: 24),
            () => RenderAsync<PitWallCommsDisplay>(width: 80, height: 24),
        })
        {
            Assert.NotEmpty(await render());
        }
    }

    [Fact]
    public void SmoothedGapsStayCloseToTheFeed()
    {
        var timing = fixture.Services.GetRequiredService<TimingDataProcessor>();
        var smoother = fixture.Services.GetRequiredService<TimingSmoother>();
        var compared = 0;

        foreach (var (number, line) in timing.Latest.Lines)
        {
            if (line.GapToLeaderSeconds() is not > 0 || line.Retired == true)
                continue;
            var smoothed = smoother.GapToLeader(number);
            Assert.NotNull(smoothed);
            // Smoothed values lag slightly behind and interpolate, so they differ by tenths at most
            Assert.InRange(smoothed.Value, (double)line.GapToLeaderSeconds()!.Value - 1.5, (double)line.GapToLeaderSeconds()!.Value + 1.5);
            compared++;
        }

        Assert.True(compared > 10);
    }

    [Fact]
    public void PitLossIsEstimatedFromObservedStops()
    {
        var timing = fixture.Services.GetRequiredService<TimingDataProcessor>();

        var estimate = RaceStrategy.EstimatePitLoss(timing.DriversByLap);

        // Several cars have stopped by lap 30 at Silverstone; real pit loss there is ~20-25s
        Assert.False(estimate.IsDefault);
        Assert.InRange(estimate.Seconds, 15, 30);
    }
}
