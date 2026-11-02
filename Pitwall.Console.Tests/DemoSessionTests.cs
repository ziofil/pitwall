using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

public class DemoSessionTests
{
    [Fact]
    public void TheBuiltInDemoUnpacksAndReplaysAsTheMexicoRace()
    {
        var directory = DemoSession.Unpack();

        Assert.True(File.Exists(Path.Join(directory, "subscribe.json")));
        Assert.True(new FileInfo(Path.Join(directory, "live.jsonl")).Length > 10_000_000);
        // Unpacking again reuses the files already there
        Assert.Equal(directory, DemoSession.Unpack());

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection()
            .AddLogging(x => x.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(configuration)
            .BuildServiceProvider();
        var sessionInfo = services.GetRequiredService<SessionInfoProcessor>();
        var lapCount = services.GetRequiredService<LapCountProcessor>();
        SessionReplay.Replay(services, directory, () => lapCount.Latest.CurrentLap > 30);

        Assert.Equal("Race", sessionInfo.Latest.Name);
        Assert.Contains("Mexico", sessionInfo.Latest.Meeting?.Name);
        Assert.NotEmpty(sessionInfo.Latest.CircuitPoints);
    }
}
