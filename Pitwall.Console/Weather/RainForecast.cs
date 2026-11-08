using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Pitwall.Console;

/// <summary>Forecast rain for one 15-minute slot.</summary>
/// <param name="Millimetres">Rain expected in the 15 minutes.</param>
/// <param name="Probability">Chance of rain, 0–100.</param>
public sealed record RainSlot(DateTimeOffset Time, double Millimetres, int? Probability);

/// <summary>
/// The rain forecast at the circuit for the next three hours in 15-minute slots, from
/// Open-Meteo, fetched in the background and refreshed every 10 minutes. Only the circuit's
/// coordinates are sent.
/// </summary>
public sealed class RainForecast(IHttpClientFactory httpClientFactory, ILogger<RainForecast> logger)
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(1);

    private (double Latitude, double Longitude)? _location;
    private IReadOnlyList<RainSlot> _slots = [];
    private Task? _fetch;
    private DateTimeOffset _nextFetch;

    /// <summary>The forecast fetched so far; starts or refreshes the fetch in the background.</summary>
    public IReadOnlyList<RainSlot> Get(double latitude, double longitude)
    {
        if (_location != (latitude, longitude))
        {
            if (_fetch is { IsCompleted: false })
                return [];
            _location = (latitude, longitude);
            _slots = [];
            _nextFetch = DateTimeOffset.MinValue;
        }

        if (_fetch is null or { IsCompleted: true } && DateTimeOffset.UtcNow >= _nextFetch)
            _fetch = FetchAsync(latitude, longitude);

        return _slots;
    }

    private async Task FetchAsync(double latitude, double longitude)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient("Public");
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.####}&longitude={longitude:0.####}&minutely_15=precipitation,precipitation_probability&forecast_minutely_15=12&timezone=UTC"
            );
            var data = JsonNode.Parse(await httpClient.GetStringAsync(url))?["minutely_15"];
            var times = data?["time"]?.AsArray() ?? [];
            var rain = data?["precipitation"]?.AsArray() ?? [];
            var probability = data?["precipitation_probability"]?.AsArray() ?? [];

            _slots =
            [
                .. times.Select(
                    (time, i) =>
                        new RainSlot(
                            DateTimeOffset.Parse(time!.GetValue<string>() + "Z"),
                            rain.ElementAtOrDefault(i)?.GetValue<double>() ?? 0,
                            probability.ElementAtOrDefault(i)?.GetValue<int>()
                        )
                ),
            ];
            _nextFetch = DateTimeOffset.UtcNow + RefreshEvery;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch the rain forecast");
            _nextFetch = DateTimeOffset.UtcNow + RetryAfterError;
        }
    }
}
