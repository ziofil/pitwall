using Microsoft.Extensions.DependencyInjection;

namespace Pitwall.Console.Tests;

/// <summary>The launch screen before any session has started.</summary>
public class LaunchScreenTests(NoSessionFixture fixture) : IClassFixture<NoSessionFixture>
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    [InlineData(160, 45)]
    public async Task OffersTheWaysToStartASession(int width, int height)
    {
        var display = fixture.Services.GetRequiredService<MainDisplay>();
        var text = await fixture.RenderAsync(display, width, height);

        Assert.Contains("Watch", text);
        Assert.Contains("Demo", text);
        Assert.Contains("Replay a session", text);
        Assert.DoesNotContain("Open the pit wall", text);
    }
}
