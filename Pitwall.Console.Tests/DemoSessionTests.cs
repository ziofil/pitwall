using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>The built-in demos: they unpack, and replay as complete sessions.</summary>
public class DemoSessionTests
{
    private static IServiceProvider Replay(DemoSession demo)
    {
        var directory = demo.Unpack();
        Assert.True(File.Exists(Path.Join(directory, "subscribe.json")));
        // Unpacking again reuses the files already there
        Assert.Equal(directory, demo.Unpack());

        var services = new ServiceCollection()
            .AddLogging(x => x.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        SessionReplay.Replay(services, directory, () => false);
        return services;
    }

    [Fact]
    public void TheRaceDemoIsTheWholeUnitedStatesGrandPrix()
    {
        var services = Replay(DemoSession.Race);

        var sessionInfo = services.GetRequiredService<SessionInfoProcessor>().Latest;
        Assert.Equal("Race", sessionInfo.Name);
        Assert.Contains("United States", sessionInfo.Meeting?.Name);
        Assert.NotEmpty(sessionInfo.CircuitPoints);

        // Lights out to the flag, every lap timed from the first
        var lapCount = services.GetRequiredService<LapCountProcessor>().Latest;
        Assert.Equal(lapCount.TotalLaps, lapCount.CurrentLap);
        var laps = services.GetRequiredService<TimingDataProcessor>().DriversByLap.Keys;
        Assert.Contains(1, laps);
        Assert.Contains(lapCount.TotalLaps!.Value, laps);

        // With car telemetry, pit stops and team radio
        Assert.NotEmpty(services.GetRequiredService<CarDataProcessor>().Latest.Entries);
        Assert.NotEmpty(services.GetRequiredService<PitStopSeriesProcessor>().Latest.PitTimes);
        Assert.NotEmpty(services.GetRequiredService<TeamRadioProcessor>().Ordered);
    }

    [Fact]
    public void TheQualifyingDemoIsTheWholeUnitedStatesQualifying()
    {
        var services = Replay(DemoSession.Qualifying);

        var sessionInfo = services.GetRequiredService<SessionInfoProcessor>().Latest;
        Assert.Equal("Qualifying", sessionInfo.Name);
        Assert.Contains("United States", sessionInfo.Meeting?.Name);

        // Through Q3: everyone set a time, and the top ten ran in Q3
        var lines = services.GetRequiredService<TimingDataProcessor>().Latest.Lines;
        Assert.True(lines.Count >= 20, $"{lines.Count} cars");
        Assert.True(
            lines.Values.Count(x => !string.IsNullOrEmpty(x.BestLapTime?.Value)) >= 20,
            "everyone set a lap time"
        );
        Assert.NotEmpty(services.GetRequiredService<CarDataProcessor>().Latest.Entries);
    }

    [Fact]
    public void EachDemoStartsWithinItsRecording()
    {
        foreach (var demo in DemoSession.All)
        {
            var live = Path.Join(demo.Unpack(), "live.jsonl");
            var first = File.ReadLines(live).First();
            var last = File.ReadLines(live).Last();
            var (_, _, start) = Pitwall.Data.JsonTimingClient.ProcessLine(first);
            var (_, _, end) = Pitwall.Data.JsonTimingClient.ProcessLine(last);
            Assert.InRange(demo.StartAt, start, end);
        }
    }
}
