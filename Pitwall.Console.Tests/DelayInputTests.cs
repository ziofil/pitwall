namespace Pitwall.Console.Tests;

public class DelayInputTests
{
    [Theory]
    [InlineData("34600", 34_600)]
    [InlineData("34600 ms", 34_600)]
    [InlineData("34.6s", 34_600)]
    [InlineData("0:34.6", 34_600)]
    [InlineData("1:02", 62_000)]
    [InlineData("0", 0)]
    public void ReadsTypedDelays(string text, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), DelayInputHandler.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-500")]
    public void RejectsOtherText(string text) => Assert.Null(DelayInputHandler.Parse(text));
}
