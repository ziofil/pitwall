using Microsoft.Extensions.Logging.Abstractions;

namespace Pitwall.Data.Tests;

public class SessionScheduleTests
{
    private sealed class FakeImporter : IDataImporter
    {
        public Task<ListMeetingsApiResponse> GetMeetingsAsync(int year) =>
            Task.FromResult(
                new ListMeetingsApiResponse
                {
                    Year = year,
                    Meetings =
                    [
                        new()
                        {
                            Key = 1,
                            Name = "São Paulo Grand Prix",
                            Location = "São Paulo",
                            Sessions = [Session("Qualifying", 7, 15), Session("Race", 8, 14)],
                        },
                    ],
                }
            );

        // Local times at Interlagos, three hours behind UTC
        private static ListMeetingsApiResponse.Meeting.Session Session(
            string name,
            int day,
            int hour
        ) =>
            new()
            {
                Key = day,
                Name = name,
                Type = name,
                StartDate = new DateTime(DateTime.UtcNow.Year, 11, day, hour, 0, 0),
                EndDate = new DateTime(DateTime.UtcNow.Year, 11, day, hour + 1, 0, 0),
                GmtOffset = TimeSpan.FromHours(-3),
            };

        public Task ImportSessionAsync(int year, int meetingKey, int sessionKey) =>
            Task.CompletedTask;

        public Task ImportSessionAsync(
            int year,
            ListMeetingsApiResponse.Meeting meeting,
            int sessionKey
        ) => Task.CompletedTask;
    }

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) =>
        new(DateTime.UtcNow.Year, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void FindsTheSessionOnNowAndTheNextOne()
    {
        var schedule = new SessionSchedule(
            new FakeImporter(),
            NullLogger<SessionSchedule>.Instance
        );

        // Qualifying runs 18:00-19:00 UTC, and counts as on a little either side
        Assert.Equal("Qualifying", schedule.Current(Utc(7, 18, 30))?.Name);
        Assert.Equal("Qualifying", schedule.Current(Utc(7, 17, 50))?.Name);
        Assert.Equal("Qualifying", schedule.Current(Utc(7, 19, 20))?.Name);
        Assert.Null(schedule.Current(Utc(7, 21)));
        Assert.Equal("Race", schedule.Next(Utc(7, 21))?.Name);
    }
}
