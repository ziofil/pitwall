using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>The live delta to each car's best lap, against the qualifying demo.</summary>
public class LapDeltaTests
{
    [Fact]
    public void TheLiveDeltaAtTheLineMatchesTheOfficialLapTimes()
    {
        var directory = DemoSession.Qualifying.Unpack();
        var services = new ServiceCollection()
            .AddLogging(x => x.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var timingService = (TimingService)services.GetRequiredService<ITimingService>();
        var clock = services.GetRequiredService<IDateTimeProvider>();
        var timing = services.GetRequiredService<TimingDataProcessor>();
        var delta = services.GetRequiredService<LapDelta>();
        timingService.ProcessSubscriptionData(
            File.ReadAllText(Path.Join(directory, "subscribe.json"))
        );

        // Just before each timing update that completes a lap, the delta then against the lap
        // time it turns out to be, less the best lap before it
        var errors = new List<double>();
        foreach (var line in File.ReadLines(Path.Join(directory, "live.jsonl")))
        {
            var (type, data, timestamp) = JsonTimingClient.ProcessLine(line);
            clock.Delay = DateTimeOffset.UtcNow - timestamp;
            var before = new Dictionary<string, (double Delta, double Best)>();
            if (type == "TimingData" && JsonNode.Parse(data!)?["Lines"] is JsonObject lines)
            {
                foreach (var (number, node) in lines)
                {
                    if (
                        node?["NumberOfLaps"] is not null
                        && delta.Live(number) is { } live
                        && timing.Latest.Lines.GetValueOrDefault(number)?.BestLapTime.ToTimeSpan()
                            is { } best
                    )
                    {
                        before[number] = (live, best.TotalSeconds);
                    }
                }
            }
            timingService.ProcessData(type, data, timestamp);
            foreach (var (number, (live, best)) in before)
            {
                if (timing.Latest.Lines[number].LastLapTime?.ToTimeSpan() is { } lap)
                    errors.Add(live - (lap.TotalSeconds - best));
            }
        }

        // Most push laps after a first timed lap: about 40 in this session
        Assert.True(errors.Count >= 35, $"{errors.Count} laps compared");
        var sizes = errors.Select(Math.Abs).Order().ToList();
        Assert.True(sizes[sizes.Count / 2] < 0.15, $"median error {sizes[sizes.Count / 2]:0.000}s");
        Assert.True(
            sizes[(int)(sizes.Count * 0.9)] < 0.35,
            $"90th percentile error {sizes[(int)(sizes.Count * 0.9)]:0.000}s"
        );
    }
}
