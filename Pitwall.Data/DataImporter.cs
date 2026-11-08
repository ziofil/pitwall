using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pitwall.Data;

public sealed class DataImporter(
    IHttpClientFactory httpClientFactory,
    IOptions<LiveTimingOptions> options,
    ILogger<DataImporter> logger
) : IDataImporter
{
    private static readonly string[] _raceTopics =
    [
        "Heartbeat",
        "CarData.z",
        "Position.z",
        "ExtrapolatedClock",
        "TopThree",
        "TimingStats",
        "TimingAppData",
        "WeatherData",
        "TrackStatus",
        "DriverList",
        "RaceControlMessages",
        "SessionData",
        "LapCount",
        "TimingData",
        "TeamRadio",
        "PitLaneTimeCollection",
        "PitStopSeries",
        "PitStop",
    ];

    private static readonly string[] _nonRaceTopics =
    [
        "Heartbeat",
        "CarData.z",
        "Position.z",
        "ExtrapolatedClock",
        "TopThree",
        "TimingStats",
        "TimingAppData",
        "WeatherData",
        "TrackStatus",
        "DriverList",
        "RaceControlMessages",
        "SessionData",
        "TimingData",
        "TeamRadio",
        "PitLaneTimeCollection",
        "PitStopSeries",
        "PitStop",
    ];

    /// <inheritdoc />
    public async Task<ListMeetingsApiResponse> GetMeetingsAsync(int year)
    {
        var httpClient = httpClientFactory.CreateClient("Default");
        var url = $"https://livetiming.formula1.com/static/{year}/Index.json";
        return await httpClient
                .GetFromJsonAsync(url, TimingDataSerializerContext.Raw.ListMeetingsApiResponse)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("An error occurred parsing the API response");
    }

    /// <inheritdoc />
    public async Task ImportSessionAsync(int year, int meetingKey, int sessionKey)
    {
        var res = await GetMeetingsAsync(year);
        var meeting =
            res.Meetings.SingleOrDefault(x => x.Key == meetingKey)
            ?? throw new KeyNotFoundException($"Meeting with key {meetingKey} not found");
        await ImportSessionAsync(year, meeting, sessionKey);
    }

    /// <inheritdoc />
    public async Task ImportSessionAsync(
        int year,
        ListMeetingsApiResponse.Meeting meeting,
        int sessionKey
    )
    {
        var session =
            meeting.Sessions.SingleOrDefault(x => x.Key == sessionKey)
            ?? throw new KeyNotFoundException($"Meeting with key {sessionKey} not found");

        if (string.IsNullOrWhiteSpace(session.Path))
        {
            throw new InvalidOperationException(
                $"This session cannot be imported as it has no Path property defined. This is usually because the session has not completed yet."
            );
        }

        var location = meeting.Location;
        var sessionName = session.Name;

        logger.LogInformation(
            "Downloading data for session {Year} {Location} {SessionName}",
            year,
            location,
            sessionName
        );

        var directory = Path.Join(
            options.Value.DataDirectory,
            $"{year}_{location}_{sessionName}".Replace(' ', '_')
        );

        var liveFilePath = Path.Join(directory, "live.jsonl");
        var subscribeFilePath = Path.Join(directory, "subscribe.json");

        if (File.Exists(liveFilePath))
        {
            logger.LogError(
                "Live data file at '{Path}' already exists. Delete this file before importing data.",
                liveFilePath
            );
            return;
        }

        if (File.Exists(subscribeFilePath))
        {
            logger.LogError(
                "Subscribe data file at '{Path}' already exists. Delete this file before importing data.",
                subscribeFilePath
            );
            return;
        }

        var prefix = $"https://livetiming.formula1.com/static/{session.Path}";

        // Records in the archive have no timestamp, only an offset from the start of the
        // session's data stream, so work out when that was. Race control messages carry the
        // official time each was issued, so they anchor it; the heartbeat's own clock is the
        // fallback, as it's sometimes far off in the archive (a day and a half for the 2026
        // Mexico City race)
        var sessionDataPoints = await GetDataAsync(prefix, "SessionInfo", DateTimeOffset.UnixEpoch)
            .ConfigureAwait(false);
        var heartbeatDataPoints = await GetDataAsync(prefix, "Heartbeat", DateTimeOffset.UnixEpoch)
            .ConfigureAwait(false);
        var raceControlDataPoints = await GetDataAsync(
                prefix,
                "RaceControlMessages",
                DateTimeOffset.UnixEpoch
            )
            .ConfigureAwait(false);

        var startDate =
            StartFromRaceControl(raceControlDataPoints)
            ?? StartFromHeartbeat(heartbeatDataPoints)
            ?? throw new InvalidOperationException(
                "Unable to find when the session's data starts: no race control messages or heartbeats"
            );
        logger.LogInformation("Found the session's data starting at {StartDate}", startDate);

        // The three streams already downloaded are moved to the start; the rest are fetched
        static List<RawTimingDataPoint> Rebase(List<RawTimingDataPoint> points, DateTimeOffset start) =>
            [.. points.Select(x => x with { DateTime = start + (x.DateTime - DateTimeOffset.UnixEpoch) })];
        var topics = (session.Type == "Race" ? _raceTopics : _nonRaceTopics).Where(x =>
            x is not ("Heartbeat" or "RaceControlMessages")
        );
        var tasks = topics
            .Select(topic => GetDataAsync(prefix, topic, startDate))
            .Append(Task.FromResult(Rebase(heartbeatDataPoints, startDate)))
            .Append(Task.FromResult(Rebase(raceControlDataPoints, startDate)))
            // Updates to the session's state during it, such as when it finishes
            .Append(Task.FromResult(Rebase(sessionDataPoints, startDate)));

        var dataPointsCollection = await Task.WhenAll(tasks).ConfigureAwait(false);
        var lines = dataPointsCollection
            .SelectMany(x => x)
            .Select(TimeTeamRadioByCapture)
            .OrderBy(x => x.DateTime)
            .Select(x =>
                JsonSerializer.Serialize(x, TimingDataSerializerContext.Raw.RawTimingDataPoint)
            )
            .ToList();

        logger.LogInformation("Saving session data to {FilePath}", liveFilePath);

        Directory.CreateDirectory(directory);

        await File.WriteAllLinesAsync(liveFilePath, lines, Encoding.UTF8).ConfigureAwait(false);

        logger.LogInformation("Saving initial session data to {FilePath}", subscribeFilePath);
        var subscribeJson = new JsonObject
        {
            ["SessionInfo"] = sessionDataPoints.First().Json.DeepClone(),
            ["Heartbeat"] = heartbeatDataPoints.First().Json.DeepClone(),
        };
        await File.WriteAllTextAsync(subscribeFilePath, subscribeJson.ToString(), Encoding.UTF8)
            .ConfigureAwait(false);

        logger.LogInformation("Written {LineCount} lines of session data", lines.Count);
    }

    private async Task<List<RawTimingDataPoint>> GetDataAsync(
        string urlPrefix,
        string type,
        DateTimeOffset startDateTime
    )
    {
        var url = $"{urlPrefix}{type}.jsonStream";
        logger.LogDebug("Downloading {Type} data from {Url}", type, url);

        try
        {
            var httpClient = httpClientFactory.CreateClient("Default");
            var rawData = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            var badLines = 0;
            var points = ParseStream(
                type,
                rawData,
                startDateTime,
                (line, ex) =>
                {
                    if (badLines++ == 0)
                    {
                        logger.LogWarning(
                            ex,
                            "Skipping {Type} lines that can't be read, the first being: {Line}",
                            type,
                            line.Length > 200 ? line[..200] : line
                        );
                    }
                }
            );
            if (badLines > 0)
                logger.LogWarning("Skipped {Count} of the {Type} lines", badLines, type);
            return points;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download {Type} data from {Url}", type, url);
            return [];
        }
    }

    /// <summary>
    /// Reads an archive stream: one record a line, each an offset from the start of the session's
    /// data (<c>hh:mm:ss.fff</c>, though some streams have more decimal places) followed by the
    /// JSON, which starts at the first brace, bracket or quote. A byte order mark is skipped.
    /// </summary>
    /// <param name="badLine">Told about each line that can't be read, which is skipped.</param>
    internal static List<RawTimingDataPoint> ParseStream(
        string type,
        string rawData,
        DateTimeOffset startDateTime,
        Action<string, Exception>? badLine = null
    )
    {
        var points = new List<RawTimingDataPoint>();
        foreach (var raw in rawData.Split('\n'))
        {
            var line = raw.Trim('\uFEFF', '\r', ' ');
            if (line.Length == 0)
                continue;
            try
            {
                var json = line.IndexOfAny(['{', '[', '"']);
                points.Add(
                    new RawTimingDataPoint(
                        type,
                        JsonNode.Parse(line[json..])!,
                        startDateTime
                            + TimeSpan.Parse(line[..json].Trim(), CultureInfo.InvariantCulture)
                    )
                );
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                badLine?.Invoke(line, ex);
            }
        }
        return points;
    }

    /// <summary>
    /// When the session's data started, from race control messages: each has the official time
    /// it was issued and arrives within a second or so, so the start is that time less the
    /// message's offset. The median over all messages smooths out the publishing delay.
    /// </summary>
    /// <param name="messages">The race control stream, read with a start of the Unix epoch.</param>
    internal static DateTimeOffset? StartFromRaceControl(List<RawTimingDataPoint> messages)
    {
        var starts = new List<DateTimeOffset>();
        foreach (var point in messages)
        {
            var offset = point.DateTime - DateTimeOffset.UnixEpoch;
            var items = point.Json["Messages"] switch
            {
                JsonArray array => array,
                JsonObject obj => obj.Select(x => x.Value),
                _ => [],
            };
            foreach (var item in items)
            {
                // The feed's times are UTC without an offset
                if (
                    item?["Utc"]?.GetValue<string>() is { } utc
                    && DateTime.TryParse(
                        utc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var issued
                    )
                )
                {
                    starts.Add(new DateTimeOffset(issued, TimeSpan.Zero) - offset);
                }
            }
        }
        return starts.Count == 0 ? null : starts.Order().ElementAt(starts.Count / 2);
    }

    /// <summary>
    /// When the session's data started, from the heartbeat's own clock. In some seasons several
    /// heartbeats share the first offset with different times; the last of those is right.
    /// </summary>
    /// <param name="heartbeats">The heartbeat stream, read with a start of the Unix epoch.</param>
    internal static DateTimeOffset? StartFromHeartbeat(List<RawTimingDataPoint> heartbeats)
    {
        if (heartbeats.Count == 0)
            return null;
        var first = heartbeats.Last(x => x.DateTime == heartbeats[0].DateTime);
        return first.Json["Utc"]?.GetValue<DateTimeOffset>()
            - (first.DateTime - DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// Team radio records are timed by the latest capture in them. Some archive streams count
    /// their offsets from a different start than the rest (the 2026 Mexico City race's was two
    /// and a half hours out), but every capture carries the time it was recorded.
    /// </summary>
    internal static RawTimingDataPoint TimeTeamRadioByCapture(RawTimingDataPoint point)
    {
        if (point.Type != "TeamRadio")
            return point;
        var captures = point.Json["Captures"] switch
        {
            JsonArray array => array,
            JsonObject obj => obj.Select(x => x.Value),
            _ => [],
        };
        var latest = captures
            .Select(x => x?["Utc"]?.GetValue<string>())
            .Select(x =>
                DateTimeOffset.TryParse(
                    x,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var utc
                )
                    ? utc
                    : (DateTimeOffset?)null
            )
            .Max();
        return latest is { } time ? point with { DateTime = time } : point;
    }
}
