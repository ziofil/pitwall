using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Pitwall.Data.Tests;

public class SessionInfoProcessorTests
{
    [Fact]
    public void VerifyDataUpdate()
    {
        // Arrange
        var processor = new SessionInfoProcessor(Substitute.For<ILogger<SessionInfoProcessor>>());

        var data = new List<SessionInfoDataPoint>()
        {
            new() { Type = "Race" },
            new() { Key = 1234 },
        };

        // Act
        foreach (var dataPoint in data)
        {
            processor.Process(dataPoint);
        }

        // Assert
        Assert.NotNull(processor.Latest);
        Assert.Equal("Race", processor.Latest.Type);
        Assert.Equal(1234, processor.Latest.Key);
    }

    [Fact]
    public void LoadsTheShippedCircuitForTheSessionsYear()
    {
        var processor = new SessionInfoProcessor(Substitute.For<ILogger<SessionInfoProcessor>>());

        // Silverstone (circuit key 2) in 2024
        processor.Process(
            new()
            {
                StartDate = new DateTime(2024, 7, 7),
                Meeting = new() { Circuit = new() { Key = 2 } },
            }
        );

        Assert.True(processor.Latest.CircuitPoints.Count > 100);
        Assert.NotEmpty(processor.Latest.CircuitCorners);
        Assert.NotNull(processor.Latest.PitLoss);
        Assert.InRange(processor.Latest.PitLoss.Normal, 15, 30);
    }

    [Fact]
    public void EveryShippedCircuitHasALayout()
    {
        // Circuit keys of tracks raced on recently
        foreach (
            var key in new[]
            {
                2,
                4,
                6,
                7,
                9,
                10,
                14,
                15,
                19,
                22,
                23,
                39,
                46,
                49,
                55,
                61,
                63,
                65,
                70,
                144,
                149,
                150,
                151,
                152,
            }
        )
        {
            var circuit = CircuitLibrary.Get(key);
            Assert.NotNull(circuit);
            var layout = circuit.LayoutFor(2025);
            Assert.NotNull(layout);
            Assert.Equal(layout.X.Count, layout.Y.Count);
        }
    }

    [Fact]
    public void LayoutForPicksTheNewestLayoutStartedByThatYear()
    {
        var circuit = new CircuitInfo(
            1,
            "Test",
            null,
            [new(2018, 0, [0, 1], [0, 1], []), new(2022, 0, [0, 2], [0, 2], [])]
        );

        Assert.Equal(2018, circuit.LayoutFor(2021)!.FromYear);
        Assert.Equal(2022, circuit.LayoutFor(2025)!.FromYear);
        Assert.Equal(2018, circuit.LayoutFor(2010)!.FromYear);
    }
}
