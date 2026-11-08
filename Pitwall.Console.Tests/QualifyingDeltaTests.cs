using Microsoft.Extensions.DependencyInjection;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>
/// The live delta to each car's best lap, on the qualifying demo in Q3 with both McLarens on
/// their final push laps.
/// </summary>
public class QualifyingDeltaTests(SampleQualifyingDemoFixture fixture)
    : IClassFixture<SampleQualifyingDemoFixture>
{
    private async Task<string> RenderAsync<T>(int width = 160, int height = 45)
        where T : IDisplay
    {
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = "McLaren";
        var display = fixture.Services.GetRequiredService<T>();
        state.CurrentScreen = display.Screen;
        return await fixture.RenderAsync(display, width, height);
    }

    [Fact]
    public void BothMcLarensAreOnAPushLap()
    {
        var delta = fixture.Services.GetRequiredService<LapDelta>();
        foreach (var number in new[] { "1", "81" })
        {
            var trace = Assert.IsType<LapDeltaTrace>(delta.Trace(number));
            Assert.InRange(trace.Live, -LapDelta.MaxDelta, LapDelta.MaxDelta);
            // Well into the lap, with the sector lines known
            Assert.True(trace.Points[^1].Distance > trace.BestLength / 2);
            Assert.Equal(2, trace.SectorEnds.Count);
        }
    }

    [Fact]
    public async Task TimingShowsTheLiveDeltaInPlaceOfMiniSectors()
    {
        var text = await RenderAsync<PitWallTimingDisplay>();

        Assert.Contains("Δ BEST", text);
        Assert.DoesNotContain("███", text);
        var norris = text.Split('\n').First(x => x.Contains("NORRIS"));
        Assert.Matches(@"[+-]\d\.\d{3}", norris);
    }

    [Fact]
    public async Task StrategyShowsTheDeltaGraphForTheTeamsCars()
    {
        var text = await RenderAsync<PitWallStrategyDisplay>();

        Assert.Contains("LAP DELTA", text);
        Assert.Contains("1 NOR", text);
        Assert.Contains("81 PIA", text);
        Assert.Contains("vs best", text);
        Assert.Contains("S2", text);
        Assert.DoesNotContain("NOT ON A PUSH LAP", text);
    }

    [Fact]
    public async Task TheDeltaGraphIsDrawnAsAnImageWhereTerminalsShowThem()
    {
        var terminal = fixture.Services.GetRequiredService<NoTerminalImages>();
        terminal.Supported = true;
        try
        {
            await RenderAsync<PitWallStrategyDisplay>();
            await fixture.Services.GetRequiredService<PitWallStrategyDisplay>()
                .PostContentDrawAsync(true);
            Assert.NotNull(terminal.LastImage);
            var directory = Path.Join(AppContext.BaseDirectory, "renders");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Join(directory, "LapDelta.png"), terminal.LastImage!);
        }
        finally
        {
            terminal.Supported = false;
        }
    }
}
