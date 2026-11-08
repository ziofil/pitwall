namespace Pitwall.Data;

/// <summary>
/// Where each circuit is, for the weather radar and forecast, keyed by F1's circuit key as in
/// <see cref="CircuitLibrary"/>.
/// </summary>
public static class CircuitLocations
{
    private static readonly Dictionary<int, (double Latitude, double Longitude)> Locations = new()
    {
        [2] = (52.0786, -1.0169), // Silverstone
        [4] = (47.5789, 19.2486), // Hungaroring
        [6] = (44.3439, 11.7167), // Imola
        [7] = (50.4372, 5.9714), // Spa-Francorchamps
        [9] = (30.1328, -97.6411), // Austin
        [10] = (-37.8497, 144.9680), // Melbourne
        [14] = (-23.7036, -46.6997), // Interlagos
        [15] = (41.5700, 2.2611), // Catalunya
        [19] = (47.2197, 14.7647), // Spielberg
        [22] = (43.7347, 7.4206), // Monte Carlo
        [23] = (45.5000, -73.5228), // Montreal
        [28] = (43.2506, 5.7917), // Paul Ricard
        [34] = (49.3278, 8.5656), // Hockenheim
        [39] = (45.6156, 9.2811), // Monza
        [46] = (34.8431, 136.5411), // Suzuka
        [49] = (31.3389, 121.2200), // Shanghai
        [55] = (52.3888, 4.5409), // Zandvoort
        [59] = (40.9517, 29.4050), // Istanbul
        [61] = (1.2914, 103.8640), // Singapore
        [63] = (26.0325, 50.5106), // Sakhir
        [65] = (19.4042, -99.0907), // Mexico City
        [70] = (24.4672, 54.6031), // Yas Marina
        [72] = (50.3356, 6.9475), // Nürburgring
        [79] = (43.4057, 39.9578), // Sochi
        [144] = (40.3725, 49.8533), // Baku
        [146] = (43.9975, 11.3719), // Mugello
        [147] = (37.2270, -8.6267), // Algarve
        [148] = (26.0325, 50.5106), // Sakhir Outer Track
        [149] = (21.6319, 39.1044), // Jeddah
        [150] = (25.4900, 51.4542), // Losail
        [151] = (25.9581, -80.2389), // Miami
        [152] = (36.1147, -115.1728), // Las Vegas
    };

    public static (double Latitude, double Longitude)? Get(int? circuitKey) =>
        circuitKey is { } key && Locations.TryGetValue(key, out var location) ? location : null;
}
