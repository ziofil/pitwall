namespace Pitwall.Data.Tests;

public class CarTelemetryLiveTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTimeOffset Utc { get; set; }
        public TimeSpan Delay { get; set; }
        public bool IsPaused => false;

        public void TogglePause() { }
    }

    /// <summary>A batch of samples of a car accelerating steadily at 20 km/h a second.</summary>
    private static CarDataPoint Batch(IEnumerable<double> seconds) =>
        new()
        {
            Entries =
            [
                .. seconds.Select(t => new CarDataPoint.Entry
                {
                    Utc = T0.AddSeconds(t),
                    Cars = new()
                    {
                        ["1"] = new()
                        {
                            Channels = new() { Speed = 100 + (int)(t * 20), Rpm = 10000 },
                        },
                    },
                }),
            ],
        };

    [Fact]
    public void LiveSpeedKeepsMovingBetweenLateUnevenBatches()
    {
        // Live: samples about 4 a second at uneven times, sent in one-second batches that arrive
        // 2-3 seconds late, and the page drawn at 15 frames a second
        var clock = new FakeClock();
        var telemetry = new CarTelemetryHistoryProcessor(new TimingDataProcessor(), clock);
        var random = new Random(1);
        var speeds = new List<int>();
        var sent = 0.0;
        for (var frame = 0; frame < 15 * 40; frame++)
        {
            var wall = frame / 15.0;
            clock.Utc = T0.AddSeconds(wall + 30);
            var arrival = sent + 1 + 2 + (random.NextDouble() * 1.0);
            if (wall >= arrival - 2.5)
            {
                telemetry.Process(
                    Batch(
                        Enumerable
                            .Range(0, 4)
                            .Select(i => sent + (i * 0.25) + (random.NextDouble() * 0.1))
                    )
                );
                sent += 1;
            }
            if (telemetry.TryGetLive("1", out var live))
                speeds.Add(live.Speed);
        }

        // After settling in, every frame moves on by about 20 km/h a second (1.3 per frame):
        // never stopping, never jumping
        var steps = speeds.Skip(15 * 15).Zip(speeds.Skip(15 * 15 + 1), (a, b) => b - a).ToList();
        Assert.All(steps, step => Assert.InRange(step, 1, 2));
    }
}
