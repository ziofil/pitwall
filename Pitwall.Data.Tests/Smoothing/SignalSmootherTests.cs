namespace Pitwall.Data.Tests;

public class SignalSmootherTests
{
    private static readonly DateTimeOffset Start = new(2024, 7, 7, 14, 0, 0, TimeSpan.Zero);

    private static SignalSmoother Smoother(double resetThreshold = 100) =>
        new(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            resetThreshold,
            blend: TimeSpan.Zero
        );

    [Fact]
    public void InterpolatesBetweenSamples()
    {
        var smoother = Smoother();
        // A value rising steadily by 1 per second, sampled once a second
        for (var i = 0; i <= 5; i++)
            smoother.Add("x", Start.AddSeconds(i), i);

        Assert.True(smoother.TryGet("x", Start.AddSeconds(2.5), out var value));
        Assert.Equal(2.5, value, precision: 6);
    }

    [Fact]
    public void FollowsCurvesWithAQuadraticFit()
    {
        var smoother = Smoother();
        for (var i = 0; i <= 5; i++)
            smoother.Add("x", Start.AddSeconds(i), i * i);

        Assert.True(smoother.TryGet("x", Start.AddSeconds(3.5), out var value));
        Assert.Equal(12.25, value, precision: 6);
    }

    [Fact]
    public void ExtrapolatesLinearlyAndOnlyBriefly()
    {
        var smoother = Smoother();
        for (var i = 0; i <= 5; i++)
            smoother.Add("x", Start.AddSeconds(i), i * i);

        // The trend at the newest sample is about 10 per second; extrapolation stops after 1s
        Assert.True(smoother.TryGet("x", Start.AddSeconds(5.5), out var soon));
        Assert.True(smoother.TryGet("x", Start.AddSeconds(30), out var later));
        Assert.InRange(soon, 29, 31);
        Assert.InRange(later, 34, 36);
    }

    [Fact]
    public void RestartsAfterADiscontinuity()
    {
        var smoother = Smoother(resetThreshold: 2);
        for (var i = 0; i <= 5; i++)
            smoother.Add("x", Start.AddSeconds(i), 1.0);
        // A pit stop: the gap jumps by 20 seconds and should show as a jump, not a ramp
        smoother.Add("x", Start.AddSeconds(6), 21.0);

        Assert.True(smoother.TryGet("x", Start.AddSeconds(5.5), out var value));
        Assert.Equal(21.0, value, precision: 6);
    }

    [Fact]
    public void ShownValueNeverJumpsWhenSamplesArrive()
    {
        // Gaps update about every 4 seconds; the value is shown every 66ms, 1.5s behind
        var smoother = new SignalSmoother(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(6),
            resetThreshold: 2,
            blend: TimeSpan.FromSeconds(1)
        );
        var random = new Random(1);
        var gap = 3.0;
        var nextSample = Start;
        double? previous = null;
        var largestStep = 0.0;

        for (var now = Start; now < Start.AddSeconds(120); now = now.AddMilliseconds(66))
        {
            if (now >= nextSample)
            {
                // A gap closing at ~0.1s per second, with timing noise
                gap -= 0.4 + (random.NextDouble() * 0.2 - 0.1);
                smoother.Add("x", now, gap);
                nextSample = now.AddSeconds(3 + (random.NextDouble() * 3));
            }

            if (smoother.TryGet("x", now.AddSeconds(-1.5), out var shown))
            {
                if (previous is not null)
                    largestStep = Math.Max(largestStep, Math.Abs(shown - previous.Value));
                previous = shown;
            }
        }

        // The value changes at ~0.1s per second, so ~0.007 per frame; allow some easing
        Assert.True(largestStep < 0.05, $"Largest step between frames was {largestStep:0.000}s");
    }

    [Fact]
    public void StartsAgainWhenTheClockJumpsBack()
    {
        var smoother = Smoother();
        // A sample far in the future, e.g. processed before a replay set its clock
        smoother.Add("x", Start.AddDays(1), 20);
        for (var i = 0; i <= 5; i++)
            smoother.Add("x", Start.AddSeconds(i), 7 + (i * 0.1));

        Assert.True(smoother.TryGet("x", Start.AddSeconds(5), out var value));
        Assert.Equal(7.5, value, precision: 3);
    }

    [Fact]
    public void SingleSampleIsHeld()
    {
        var smoother = Smoother();
        smoother.Add("x", Start, 3.2);

        Assert.True(smoother.TryGet("x", Start.AddSeconds(4), out var value));
        Assert.Equal(3.2, value);
        Assert.False(smoother.TryGet("missing", Start, out _));
    }
}
