using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.Data;

/// <summary>Typical time lost by a pit stop at a circuit, in seconds.</summary>
public sealed record PitLossTimes(double Normal, double SafetyCar, double VirtualSafetyCar);

/// <summary>A corner number and where it is, in the same coordinates as the car positions.</summary>
public sealed record CircuitCorner(int Number, float X, float Y);

/// <summary>A circuit's outline as used from <see cref="FromYear"/> until the next layout.</summary>
public sealed record CircuitLayout(
    int FromYear,
    int Rotation,
    List<int> X,
    List<int> Y,
    List<CircuitCorner> Corners
);

/// <summary>Everything stored about a circuit, keyed by F1's circuit key.</summary>
public sealed record CircuitInfo(
    int Key,
    string Name,
    PitLossTimes? PitLoss,
    List<CircuitLayout> Layouts
)
{
    /// <summary>
    /// The layout in use in the given year: the newest one starting in or before it, or the
    /// oldest one we have if the year is earlier than all of them.
    /// </summary>
    public CircuitLayout? LayoutFor(int year) =>
        Layouts.Where(x => x.FromYear <= year).MaxBy(x => x.FromYear)
        ?? Layouts.MinBy(x => x.FromYear);
}

/// <summary>
/// Circuit outlines, corners and typical pit loss times, shipped with Pitwall as embedded files
/// (<c>Pitwall.Data/Circuits/*.json</c>) so the track page works offline and doesn't depend on any
/// third-party server. The files come from the MultiViewer API and are refreshed with
/// <c>dotnet run --project gen/Pitwall.CodeGenerator -- circuits</c>.
/// </summary>
public static class CircuitLibrary
{
    private static readonly Lazy<Dictionary<int, CircuitInfo>> _circuits = new(Load);

    public static CircuitInfo? Get(int circuitKey) => _circuits.Value.GetValueOrDefault(circuitKey);

    private static Dictionary<int, CircuitInfo> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var circuits = new Dictionary<int, CircuitInfo>();
        foreach (
            var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith("Circuits."))
        )
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var circuit = JsonSerializer.Deserialize(
                stream,
                CircuitLibraryContext.Default.CircuitInfo
            );
            if (circuit is not null)
                circuits[circuit.Key] = circuit;
        }
        return circuits;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CircuitInfo))]
public partial class CircuitLibraryContext : JsonSerializerContext { }
