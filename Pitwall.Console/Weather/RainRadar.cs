using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Pitwall.Console;

/// <summary>A radar image of rain around the circuit at one moment.</summary>
/// <param name="ToNext">How the rain moved from this frame to the next, if there is one.</param>
public sealed record RadarFrame(
    DateTimeOffset Time,
    SKImage Image,
    RadarPixels Pixels,
    RadarFlow? ToNext = null
);

/// <summary>
/// The rain radar around a circuit: a dark map and the radar images from the last two hours,
/// each covering the same square centred on the circuit, <see cref="RainRadar.RadiusKm"/> to each side.
/// </summary>
public sealed record RadarImages(SKImage? Map, IReadOnlyList<RadarFrame> Frames, string? Error);

/// <summary>
/// Fetches rain radar from RainViewer (10-minute frames covering the last two hours) and a dark
/// base map from CARTO, in the background, refreshing every few minutes. Only map tile
/// coordinates are sent; requests go through the "Public" client so the F1 token never does.
/// </summary>
public sealed class RainRadar(IHttpClientFactory httpClientFactory, ILogger<RainRadar> logger)
{
    public const double RadiusKm = 120;

    // RainViewer's free radar tiles stop at zoom 7; the map is fetched sharper
    private const int RadarZoom = 7;
    private const int MapZoom = 9;
    private const int TileSize = 256;

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(1);

    private (double Latitude, double Longitude)? _location;
    private RadarImages _images = new(null, [], null);
    private Task? _fetch;
    private DateTimeOffset _nextFetch;

    /// <summary>
    /// The radar for a location as fetched so far. Starts or refreshes the fetch in the
    /// background as needed, so this never waits on the network.
    /// </summary>
    public RadarImages Get(double latitude, double longitude)
    {
        if (_location != (latitude, longitude))
        {
            if (_fetch is { IsCompleted: false })
                return new(null, [], null);
            _location = (latitude, longitude);
            _images = new(null, [], null);
            _nextFetch = DateTimeOffset.MinValue;
        }

        if (_fetch is null or { IsCompleted: true } && DateTimeOffset.UtcNow >= _nextFetch)
            _fetch = FetchAsync(latitude, longitude);

        return _images;
    }

    private async Task FetchAsync(double latitude, double longitude)
    {
        var httpClient = httpClientFactory.CreateClient("Public");
        try
        {
            var map =
                _images.Map
                ?? await MosaicAsync(
                    httpClient,
                    MapZoom,
                    latitude,
                    longitude,
                    (z, x, y) => $"https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png"
                );

            var index = JsonNode.Parse(
                await httpClient.GetStringAsync(
                    "https://api.rainviewer.com/public/weather-maps.json"
                )
            );
            var host = index?["host"]?.GetValue<string>();
            var past = index?["radar"]?["past"]?.AsArray() ?? [];

            var frames = new List<RadarFrame>();
            foreach (var entry in past)
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(entry!["time"]!.GetValue<long>());
                var path = entry["path"]!.GetValue<string>();
                var existing = _images.Frames.FirstOrDefault(x => x.Time == time);
                if (existing is not null)
                {
                    frames.Add(existing);
                    continue;
                }
                var image = await MosaicAsync(
                    httpClient,
                    RadarZoom,
                    latitude,
                    longitude,
                    (z, x, y) => $"{host}{path}/{TileSize}/{z}/{x}/{y}/2/1_0.png"
                );
                frames.Add(new RadarFrame(time, image, RadarPixels.From(image)));
            }

            // The motion between consecutive frames, for drawing the frames in between; kept
            // from the last fetch where the pair is the same
            for (var i = 0; i < frames.Count - 1; i++)
            {
                if (frames[i].ToNext is not null && WasFollowedBy(frames[i], frames[i + 1]))
                    continue;
                frames[i] = frames[i] with
                {
                    ToNext = RadarMotion.Estimate(frames[i].Pixels, frames[i + 1].Pixels),
                };
            }

            _images = new(map, frames, null);
            _nextFetch = DateTimeOffset.UtcNow + RefreshEvery;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch the rain radar");
            _images = _images with { Error = "Radar unavailable" };
            _nextFetch = DateTimeOffset.UtcNow + RetryAfterError;
        }
    }

    private bool WasFollowedBy(RadarFrame frame, RadarFrame next)
    {
        var frames = _images.Frames;
        var index = frames.ToList().FindIndex(x => x.Time == frame.Time);
        return index >= 0 && index + 1 < frames.Count && frames[index + 1].Time == next.Time;
    }

    /// <summary>
    /// Stitches the map tiles covering the square around the location and crops it to exactly
    /// that square, so images from different zoom levels line up when drawn over each other.
    /// </summary>
    private static async Task<SKImage> MosaicAsync(
        HttpClient httpClient,
        int zoom,
        double latitude,
        double longitude,
        Func<int, int, int, string> tileUrl
    )
    {
        // Web Mercator: the location in pixels of the whole world map at this zoom
        var worldSize = TileSize * Math.Pow(2, zoom);
        var centreX = (longitude + 180) / 360 * worldSize;
        var centreY =
            (1 - (Math.Asinh(Math.Tan(latitude * Math.PI / 180)) / Math.PI)) / 2 * worldSize;
        var metresPerPixel = 40_075_016.686 * Math.Cos(latitude * Math.PI / 180) / worldSize;
        var radius = RadiusKm * 1000 / metresPerPixel;
        var left = centreX - radius;
        var top = centreY - radius;
        var size = (int)Math.Ceiling(radius * 2);

        using var surface = SKSurface.Create(
            new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        surface.Canvas.Clear(SKColors.Transparent);
        for (var tileX = (int)(left / TileSize); tileX <= (int)((left + size) / TileSize); tileX++)
        {
            for (
                var tileY = (int)(top / TileSize);
                tileY <= (int)((top + size) / TileSize);
                tileY++
            )
            {
                using var response = await httpClient.GetAsync(tileUrl(zoom, tileX, tileY));
                if (!response.IsSuccessStatusCode)
                    continue;
                using var tile = SKImage.FromEncodedData(
                    await response.Content.ReadAsByteArrayAsync()
                );
                if (tile is not null)
                    surface.Canvas.DrawImage(
                        tile,
                        (float)((tileX * TileSize) - left),
                        (float)((tileY * TileSize) - top)
                    );
            }
        }
        return surface.Snapshot();
    }
}
