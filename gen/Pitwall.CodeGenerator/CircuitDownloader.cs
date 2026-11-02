using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pitwall.Data;

namespace Pitwall.CodeGenerator;

/// <summary>
/// Downloads circuit outlines, corners and pit loss times from the MultiViewer API into
/// <c>Pitwall.Data/Circuits</c>, one file per circuit. Each file keeps only the distinct layouts,
/// each with the first year it was used.
/// </summary>
public sealed class CircuitDownloader
{
    private const string ApiBase = "https://api.multiviewer.app/api/v1";
    private const int FirstYear = 2018;

    private readonly HttpClient _http = new();

    public CircuitDownloader() =>
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("pitwall-circuit-downloader");

    public async Task DownloadAsync(string repositoryRoot)
    {
        var outputDirectory = Path.GetFullPath(Path.Join(repositoryRoot, "Pitwall.Data/Circuits"));
        Directory.CreateDirectory(outputDirectory);

        var list = JsonNode.Parse(await _http.GetStringAsync($"{ApiBase}/circuits"))!.AsObject();
        foreach (var (_, entry) in list)
        {
            var key = entry!["circuitKey"]!.GetValue<int>();
            var name = entry["name"]!.GetValue<string>();
            var layouts = new List<CircuitLayout>();

            for (var year = FirstYear; year <= DateTime.UtcNow.Year; year++)
            {
                var response = await _http.GetAsync($"{ApiBase}/circuits/{key}/{year}");
                if (!response.IsSuccessStatusCode)
                    continue;
                var layout = ParseLayout(
                    JsonNode.Parse(await response.Content.ReadAsStringAsync())!,
                    year
                );
                if (layout is null)
                    continue;

                // Keep a layout only when the outline changes
                var previous = layouts.LastOrDefault();
                if (
                    previous is null
                    || !previous.X.SequenceEqual(layout.X)
                    || !previous.Y.SequenceEqual(layout.Y)
                )
                    layouts.Add(layout);
            }

            if (layouts.Count == 0)
            {
                await Terminal.OutLineAsync($"  {key, 3} {name}: no layouts, skipped");
                continue;
            }

            var circuit = new CircuitInfo(key, name, ParsePitLoss(entry["pitLoss"]), layouts);
            var json = JsonSerializer.Serialize(circuit, CircuitLibraryContext.Default.CircuitInfo);
            await File.WriteAllTextAsync(Path.Join(outputDirectory, $"{key}.json"), json);
            await Terminal.OutLineAsync(
                $"  {key, 3} {name}: {layouts.Count} layout(s) from {string.Join(", ", layouts.Select(x => x.FromYear))}"
            );
        }
    }

    private static CircuitLayout? ParseLayout(JsonNode node, int year)
    {
        var x = node["x"]?.AsArray().Select(v => (int)Math.Round(v!.GetValue<double>())).ToList();
        var y = node["y"]?.AsArray().Select(v => (int)Math.Round(v!.GetValue<double>())).ToList();
        if (x is null || y is null || x.Count < 2)
            return null;

        var corners =
            node["corners"]
                ?.AsArray()
                .Select(c => new CircuitCorner(
                    c!["number"]!.GetValue<int>(),
                    (float)Math.Round(c["trackPosition"]!["x"]!.GetValue<double>()),
                    (float)Math.Round(c["trackPosition"]!["y"]!.GetValue<double>())
                ))
                .ToList()
            ?? [];

        var rotation = (int)Math.Round(node["rotation"]?.GetValue<double>() ?? 0);
        return new CircuitLayout(year, rotation, x, y, corners);
    }

    private static PitLossTimes? ParsePitLoss(JsonNode? node)
    {
        static double? Parse(JsonNode? value) =>
            double.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;

        return
            node is not null
            && Parse(node["normal"]) is { } normal
            && Parse(node["sc"]) is { } safetyCar
            && Parse(node["vsc"]) is { } virtualSafetyCar
            ? new PitLossTimes(normal, safetyCar, virtualSafetyCar)
            : null;
    }
}
