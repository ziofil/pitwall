using System.Globalization;

namespace Pitwall.Data;

/// <summary>The weather at one moment of the session.</summary>
public readonly record struct WeatherSample(
    DateTimeOffset Utc,
    double? AirTemp,
    double? TrackTemp,
    double? Humidity,
    double? Pressure,
    double? WindSpeed,
    int? WindDirection,
    bool Rainfall
);

/// <summary>
/// Keeps every weather update of the session, so trends and rain spells can be shown.
/// <see cref="WeatherProcessor"/> only keeps the latest values. The feed sends weather about
/// once a minute, so the history stays small.
/// </summary>
public sealed class WeatherHistoryProcessor(IDateTimeProvider dateTimeProvider)
    : IProcessor<WeatherDataPoint>
{
    private readonly List<WeatherSample> _samples = [];
    private readonly Lock _lock = new();

    public WeatherDataPoint Latest { get; } = new();

    public IReadOnlyList<WeatherSample> Samples
    {
        get
        {
            lock (_lock)
                return [.. _samples];
        }
    }

    public void Process(WeatherDataPoint data)
    {
        lock (_lock)
        {
            Latest.MergeWith(data);
            var utc = dateTimeProvider.Utc;
            // After seeking back in a replay, the newer history no longer applies
            _samples.RemoveAll(x => x.Utc > utc);
            _samples.Add(
                new WeatherSample(
                    utc,
                    Number(Latest.AirTemp),
                    Number(Latest.TrackTemp),
                    Number(Latest.Humidity),
                    Number(Latest.Pressure),
                    Number(Latest.WindSpeed),
                    (int?)Number(Latest.WindDirection),
                    Latest.Rainfall is "1"
                )
            );
        }
    }

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            ? x
            : null;
}
