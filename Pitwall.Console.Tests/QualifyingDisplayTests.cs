using Microsoft.Extensions.DependencyInjection;

namespace Pitwall.Console.Tests;

/// <summary>
/// Renders the pit wall pages against the replayed 2024 Imola qualifying session, for McLaren,
/// to cover the non-race layouts.
/// </summary>
public class QualifyingDisplayTests(SampleQualifyingFixture fixture)
    : IClassFixture<SampleQualifyingFixture>
{
    private async Task<string> RenderAsync<T>()
        where T : IDisplay
    {
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = "McLaren";
        var display = fixture.Services.GetRequiredService<T>();
        state.CurrentScreen = display.Screen;
        return await fixture.RenderAsync(display);
    }

    [Fact]
    public async Task TimingPageUsesTheBestLapLayout()
    {
        var text = await RenderAsync<PitWallTimingDisplay>();

        Assert.Contains("BEST LAP", text);
        Assert.DoesNotContain("LAST LAP", text);
        Assert.Contains("L. NORRIS", text);
    }

    [Fact]
    public async Task StrategyPageExplainsTheRaceTraceIsForRaces()
    {
        var text = await RenderAsync<PitWallStrategyDisplay>();

        Assert.Contains("ONLY AVAILABLE IN RACES", text);
        Assert.Contains("NOR", text);
    }

    [Fact]
    public async Task OtherPagesRender()
    {
        // This recording has no car telemetry or positions, so those pages explain why they're empty
        Assert.Contains("No car telemetry received yet", await RenderAsync<PitWallTelemetryDisplay>());
        Assert.Contains(
            fixture.HasCircuitMap ? "No car position data received yet" : "No circuit map available",
            await RenderAsync<PitWallTrackMapDisplay>()
        );
        Assert.Contains("RACE CONTROL", await RenderAsync<PitWallCommsDisplay>());
    }
}
