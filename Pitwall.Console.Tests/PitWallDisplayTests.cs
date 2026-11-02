using Microsoft.Extensions.DependencyInjection;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>
/// Renders each pit wall page against the replayed 2024 Silverstone race, for Mercedes
/// (Hamilton and Russell), and checks the key information is shown.
/// </summary>
public class PitWallDisplayTests(SampleRaceFixture fixture) : IClassFixture<SampleRaceFixture>
{
    private async Task<string> RenderAsync<T>(
        string team = "Mercedes",
        int width = 160,
        int height = 45
    )
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
    public async Task LaunchScreenShowsTheMenu()
    {
        var text = await RenderAsync<MainDisplay>();

        Assert.Contains("██████╗", text);
        Assert.Contains("Open the pit wall", text);
        Assert.Contains("Quit", text);
        // A session is running, so another can't be started on top of it
        Assert.DoesNotContain("Replay a session", text);
        Assert.DoesNotContain("Watch a live session", text);
        Assert.Contains("restart Pitwall to watch a different session", text);
        // Debug screens are no longer in the menu
        Assert.DoesNotContain("Raw data", text);
        Assert.DoesNotContain("Logs", text);
        Assert.DoesNotContain(
            "undercut",
            text.Replace("a fork of undercut-f1", ""),
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Theory]
    [InlineData(45, "0:45")]
    [InlineData(3750, "1:02:30")]
    [InlineData(90061, "1d 01:01:01")]
    public void DelayIsFormattedCompactly(int seconds, string expected) =>
        Assert.Equal(expected, State.FormatDelay(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task HeaderHighlightsTheDelayRightAfterAChange()
    {
        var state = fixture.Services.GetRequiredService<State>();
        state.DelayChangedAt = DateTimeOffset.UtcNow;

        var text = await RenderAsync<PitWallTimingDisplay>(width: 220);

        Assert.Contains(" DELAY ", text);
    }

    [Fact]
    public async Task CommsPageOffersTheTranscriptionModelWhenMissing()
    {
        var text = await RenderAsync<PitWallCommsDisplay>(width: 200);

        Assert.Contains("download the transcription model", text);
        Assert.DoesNotContain("T transcribe", text);
    }

    [Fact]
    public async Task SettingsScreenListsEverySetting()
    {
        var text = await RenderAsync<SettingsDisplay>();

        Assert.Contains("Data folder", text);
        Assert.Contains("Image protocol", text);
        Assert.Contains("after a restart", text);
        Assert.Contains("Saved to", text);
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
        Assert.Contains("STOPS", text);
    }

    [Fact]
    public async Task TelemetryPageShowsChannelsForBothCars()
    {
        var text = await RenderAsync<PitWallTelemetryDisplay>();

        Assert.Contains("Waveform · 44 HAM", text);
        Assert.Contains("Waveform · 63 RUS", text);
        Assert.Contains("vCar", text);
        Assert.Contains("Best speeds", text);
        Assert.Contains("kph P", text);
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
    public void TrackImageRendersTheRingAndCircuit()
    {
        var display = fixture.Services.GetRequiredService<PitWallTrackMapDisplay>();
        fixture.Services.GetRequiredService<State>().SelectedTeam = "Mercedes";
        var scene = display.CurrentScene();
        if (scene is null)
        {
            Assert.False(fixture.HasCircuitMap);
            return;
        }

        using var image = TrackMapRenderer.Render(scene, 1440, 780);
        Assert.Equal(1440, image.Width);
        Assert.Contains(scene.Cars, x => x.TeamCar);

        // Saved next to the other renders, for looking at
        var directory = Path.Join(AppContext.BaseDirectory, "renders");
        Directory.CreateDirectory(directory);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Join(directory, "TrackImage.png"), data.ToArray());
    }

    [Theory]
    [InlineData(typeof(PitWallStrategyDisplay))]
    [InlineData(typeof(PitWallTelemetryDisplay))]
    [InlineData(typeof(PitWallTrackMapDisplay))]
    public async Task ChartsAreDrawnAsImagesWhenTheTerminalSupportsThem(Type displayType)
    {
        var terminal = fixture.Services.GetRequiredService<NoTerminalImages>();
        var state = fixture.Services.GetRequiredService<State>();
        state.SelectedTeam = "Mercedes";
        var display = (IDisplay)fixture.Services.GetRequiredService(displayType);
        state.CurrentScreen = display.Screen;
        try
        {
            terminal.Supported = true;
            // The first frame includes one-off work (loading code, fonts, the track's static
            // layer), so it isn't counted
            await display.GetContentAsync();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const int frames = 10;
            for (var i = 0; i < frames; i++)
                await display.GetContentAsync();
            var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;

            if (displayType == typeof(PitWallTrackMapDisplay) && !fixture.HasCircuitMap)
                return;
            Assert.NotNull(terminal.LastImage);

            // Saved next to the other renders, for looking at
            var directory = Path.Join(AppContext.BaseDirectory, "renders");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(
                Path.Join(directory, $"{display.Screen}-image.png"),
                terminal.LastImage
            );
            await File.WriteAllTextAsync(
                Path.Join(directory, $"{display.Screen}-frame-ms.txt"),
                $"{perFrame:0.0}"
            );

            // Catches a frame becoming pathologically slow. It's typically ~20ms; shared CI
            // machines vary a lot, and the app lowers its frame rate on slow machines anyway, so
            // the bound is generous rather than the 66ms frame budget
            Assert.True(perFrame < 150, $"{perFrame:0.0}ms per frame");
        }
        finally
        {
            terminal.Supported = false;
        }
    }

    [Fact]
    public async Task CommsPageShowsRaceControlAndTeamRadio()
    {
        var text = await RenderAsync<PitWallCommsDisplay>();

        Assert.Contains("RACE CONTROL", text);
        Assert.Contains("TEAM RADIO", text);
        Assert.Contains("44 HAM", text);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(140)]
    [InlineData(200)]
    public async Task TimingColumnsLineUpWithTheirHeaders(int width)
    {
        var text = await RenderAsync<PitWallTimingDisplay>(width: width, height: 30);
        var lines = text.Split('\n');
        var header = lines.First(x => x.Contains("POS"));
        var hamilton = lines.First(x => x.Contains("HAMILTON"));

        // Each value ends in the same column as its right-aligned header
        foreach (var column in new[] { "LAPS", "PIT" })
        {
            var end = header.IndexOf(column, StringComparison.Ordinal) + column.Length;
            Assert.True(
                end <= hamilton.Length && hamilton[end - 1] != ' ',
                $"{column} misaligned at width {width}:\n{header}\n{hamilton}"
            );
        }
        Assert.Equal(
            header.IndexOf("DRIVER", StringComparison.Ordinal),
            hamilton.IndexOf("L. HAMILTON", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task PagesRenderInASmallTerminal()
    {
        // Mostly checks nothing throws when the space is tight
        foreach (
            var render in new Func<Task<string>>[]
            {
                () => RenderAsync<PitWallTimingDisplay>(width: 80, height: 24),
                () => RenderAsync<PitWallStrategyDisplay>(width: 80, height: 24),
                () => RenderAsync<PitWallTelemetryDisplay>(width: 80, height: 24),
                () => RenderAsync<PitWallTrackMapDisplay>(width: 80, height: 24),
                () => RenderAsync<PitWallCommsDisplay>(width: 80, height: 24),
            }
        )
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
            if (line.GapToLeaderSeconds() is not { } raw || raw <= 0 || line.Retired == true)
                continue;
            var smoothed = smoother.GapToLeader(number);
            Assert.NotNull(smoothed);
            // Smoothed values lag slightly behind and interpolate, so they differ by tenths at most
            Assert.InRange(smoothed!.Value, (double)raw - 1.5, (double)raw + 1.5);
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
