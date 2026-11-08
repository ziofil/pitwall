using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>Space syncs the pit wall to a broadcast at the start.</summary>
public class SyncToStartTests
{
    private static readonly DateTimeOffset LightsOut = new(2026, 10, 25, 20, 3, 49, 706, TimeSpan.Zero);

    [Fact]
    public void ReadsStartsFromTheSnapshotAndFromUpdates()
    {
        var starts = new SessionStarts();
        // The first snapshot has the status series as a list, updates key it by index
        starts.Observe(
            "SessionData",
            """{"StatusSeries":[{"Utc":"2026-10-25T19:06:54Z","TrackStatus":"Yellow"}]}"""
        );
        starts.Observe(
            "SessionData",
            """{"StatusSeries":{"4":{"Utc":"2026-10-25T20:03:49.706Z","SessionStatus":"Started"}}}"""
        );
        starts.Observe(
            "SessionData",
            """{"StatusSeries":{"23":{"Utc":"2026-10-25T21:39:06.656Z","SessionStatus":"Finished"}}}"""
        );
        starts.Observe("TimingData", """{"StatusSeries":{"1":{"SessionStatus":"Started"}}}""");

        Assert.Equal([LightsOut], starts.All);
    }

    [Fact]
    public void SpaceAtLightsOutSetsTheDelayToTheBroadcast()
    {
        var starts = new SessionStarts();
        starts.Observe(
            "SessionData",
            """{"StatusSeries":{"4":{"Utc":"2026-10-25T20:03:49.706Z","SessionStatus":"Started"}}}"""
        );
        var clock = new DateTimeProvider(NullLogger<DateTimeProvider>.Instance);
        var state = new State();
        // The pit wall is running near the start, but out of step with the TV
        var now = LightsOut + TimeSpan.FromSeconds(45.2);
        clock.Delay = DateTimeOffset.UtcNow - LightsOut + TimeSpan.FromSeconds(12);

        Assert.True(new SyncToStartInputHandler(clock, starts, state).Sync(now));

        Assert.Equal(TimeSpan.FromSeconds(45.2), clock.Delay);
        Assert.True(DateTimeOffset.UtcNow - state.DelayChangedAt < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WithoutAStartNothingChanges()
    {
        var clock = new DateTimeProvider(NullLogger<DateTimeProvider>.Instance) { Delay = TimeSpan.FromSeconds(30) };
        var handler = new SyncToStartInputHandler(clock, new SessionStarts(), new State());

        Assert.False(handler.IsEnabled);
        Assert.False(handler.Sync(DateTimeOffset.UtcNow));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Delay);
    }

    [Fact]
    public void TheRaceDemoKnowsWhenTheLightsWentOut()
    {
        var services = new ServiceCollection()
            .AddLogging(x => x.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        SessionReplay.Replay(services, DemoSession.Race.Unpack(), () => false);

        var starts = services.GetRequiredService<SessionStarts>().All;
        Assert.Contains(LightsOut, starts);
    }
}
