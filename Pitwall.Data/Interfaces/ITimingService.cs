using Microsoft.Extensions.Hosting;

namespace Pitwall.Data;

public interface ITimingService : IHostedService
{
    List<(string type, string? data, DateTimeOffset timestamp)> GetQueueSnapshot();

    Task EnqueueAsync(string type, string? data, DateTimeOffset timestamp);

    int GetRemainingWorkItems();

    void ProcessSubscriptionData(string res);
}
