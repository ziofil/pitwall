using Microsoft.Extensions.Logging;

namespace Pitwall.Data;

/// <summary>A session from F1's schedule, with its times in UTC.</summary>
public sealed record ScheduledSession(
    string Location,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End
);

/// <summary>
/// Which session is on now and which is next, from F1's schedule for the year. Fetched in the
/// background and refreshed every few minutes, so asking never waits on the network.
/// </summary>
public sealed class SessionSchedule(IDataImporter importer, ILogger<SessionSchedule> logger)
{
    /// <summary>A session counts as on from this long before its start.</summary>
    private static readonly TimeSpan OnBefore = TimeSpan.FromMinutes(15);

    /// <summary>...and until this long after its scheduled end, which sessions often overrun.</summary>
    private static readonly TimeSpan OnAfter = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);

    private IReadOnlyList<ScheduledSession> _sessions = [];
    private Task? _fetch;
    private DateTimeOffset _nextFetch;

    /// <summary>The session on now, if any.</summary>
    public ScheduledSession? Current(DateTimeOffset now) =>
        Sessions().FirstOrDefault(x => now >= x.Start - OnBefore && now <= x.End + OnAfter);

    /// <summary>The next session to start.</summary>
    public ScheduledSession? Next(DateTimeOffset now) =>
        Sessions().Where(x => x.Start > now).MinBy(x => x.Start);

    private IReadOnlyList<ScheduledSession> Sessions()
    {
        if (_fetch is null or { IsCompleted: true } && DateTimeOffset.UtcNow >= _nextFetch)
            _fetch = FetchAsync();
        return _sessions;
    }

    private async Task FetchAsync()
    {
        _nextFetch = DateTimeOffset.UtcNow + RefreshEvery;
        try
        {
            var meetings = await importer.GetMeetingsAsync(DateTimeOffset.UtcNow.Year);
            _sessions =
            [
                .. meetings.Meetings.SelectMany(meeting =>
                    meeting.Sessions.Select(session => new ScheduledSession(
                        meeting.Location,
                        session.Name,
                        new DateTimeOffset(session.StartDate, session.GmtOffset),
                        new DateTimeOffset(session.EndDate, session.GmtOffset)
                    ))
                ),
            ];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch the session schedule");
        }
    }
}
