namespace Pitwall.Data.Tests;

public class DataImporterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 18, 50, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsAStreamThatStartsWithAByteOrderMark()
    {
        // As the archive serves the 2026 Mexico City race's team radio
        var raw =
            "﻿00:00:12.345{\"Captures\":[]}\r\n00:01:00.000{\"Captures\":{\"1\":{}}}\n";

        var points = DataImporter.ParseStream("TeamRadio", raw, Start);

        Assert.Equal(2, points.Count);
        Assert.Equal(Start + TimeSpan.FromSeconds(12.345), points[0].DateTime);
        Assert.Equal(Start + TimeSpan.FromMinutes(1), points[1].DateTime);
        Assert.All(points, x => Assert.Equal("TeamRadio", x.Type));
    }

    [Theory]
    [InlineData("00:00:12.3456789{\"Captures\":[]}")]
    [InlineData("00:00:12.3456789\"H4sIAAAAAAAA\"")]
    [InlineData("00:00:12.3456789[1,2]")]
    public void ReadsOffsetsWithMoreDecimalPlaces(string line)
    {
        var point = Assert.Single(DataImporter.ParseStream("TeamRadio", line, Start));

        Assert.Equal(Start + TimeSpan.FromTicks(123456789), point.DateTime);
    }

    [Fact]
    public void SkipsLinesThatCantBeReadAndKeepsTheRest()
    {
        var bad = new List<string>();
        var points = DataImporter.ParseStream(
            "TeamRadio",
            "not a record\n00:00:01.000{\"a\":1}\n00:00:02.000{broken\n",
            Start,
            (line, _) => bad.Add(line)
        );

        Assert.Single(points);
        Assert.Equal(["not a record", "00:00:02.000{broken"], bad);
    }

    [Fact]
    public void FindsTheStartFromRaceControlMessages()
    {
        // Messages are published a moment after the time they carry; the first record is the
        // snapshot (a list), later ones are updates keyed by index
        var raw = """
            00:05:00.400{"Messages":[{"Utc":"2026-11-01T18:55:00","Message":"GREEN LIGHT"}]}
            00:20:00.700{"Messages":{"1":{"Utc":"2026-11-01T19:10:00","Message":"DRS ENABLED"}}}
            00:40:00.500{"Messages":{"2":{"Utc":"2026-11-01T19:30:00","Message":"YELLOW"}}}
            """;

        var start = DataImporter.StartFromRaceControl(
            DataImporter.ParseStream("RaceControlMessages", raw, DateTimeOffset.UnixEpoch)
        );

        // The median: issued at :00, received half a second later
        Assert.Equal(Start - TimeSpan.FromSeconds(0.5), start);
    }

    [Fact]
    public void FallsBackToTheHeartbeatWithoutRaceControlMessages()
    {
        var heartbeats = DataImporter.ParseStream(
            "Heartbeat",
            "00:00:14.000{\"Utc\":\"2026-11-01T18:50:14Z\"}\n",
            DateTimeOffset.UnixEpoch
        );

        Assert.Null(DataImporter.StartFromRaceControl([]));
        Assert.Equal(Start, DataImporter.StartFromHeartbeat(heartbeats));
    }

    [Fact]
    public void TimesTeamRadioByItsCaptures()
    {
        // Stamped two and a half hours late, as in the 2026 Mexico City race's archive
        var point = Assert.Single(
            DataImporter.ParseStream(
                "TeamRadio",
                "02:32:53.7260000{\"Captures\":{\"1\":{\"Utc\":\"2026-11-01T19:55:24.566Z\",\"RacingNumber\":\"50\"}}}",
                Start
            )
        );

        var timed = DataImporter.TimeTeamRadioByCapture(point);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 19, 55, 24, 566, TimeSpan.Zero), timed.DateTime);
        // Other streams keep their own times
        var other = point with { Type = "TimingData" };
        Assert.Equal(other.DateTime, DataImporter.TimeTeamRadioByCapture(other).DateTime);
    }
}
