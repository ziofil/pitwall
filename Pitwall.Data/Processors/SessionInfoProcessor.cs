using Microsoft.Extensions.Logging;

namespace Pitwall.Data;

public class SessionInfoProcessor(ILogger<SessionInfoProcessor> logger)
    : ProcessorBase<SessionInfoDataPoint>()
{
    public override void Process(SessionInfoDataPoint data)
    {
        base.Process(data);

        if (Latest.CircuitPoints.Count == 0 && Latest.Meeting?.Circuit?.Key is { } circuitKey)
        {
            LoadCircuit(circuitKey, Latest.StartDate);
        }
    }

    /// <summary>
    /// Fills in the circuit outline, corners and pit loss times from the circuit files shipped
    /// with Pitwall, using the layout in use in the session's year.
    /// </summary>
    private void LoadCircuit(int circuitKey, DateTime? sessionDate)
    {
        var circuit = CircuitLibrary.Get(circuitKey);
        var year = (sessionDate ?? DateTime.UtcNow).Year;
        var layout = circuit?.LayoutFor(year);
        if (circuit is null || layout is null)
        {
            logger.LogWarning("No circuit data shipped for circuit key {CircuitKey}", circuitKey);
            return;
        }

        logger.LogInformation(
            "Loaded circuit {Name} ({CircuitKey}), layout from {FromYear} for {Year}",
            circuit.Name,
            circuitKey,
            layout.FromYear,
            year
        );
        Latest.CircuitPoints = layout.X.Zip(layout.Y).ToList();
        Latest.CircuitCorners = layout.Corners.Select(x => (x.Number, x.X, x.Y)).ToList();
        Latest.CircuitRotation = layout.Rotation;
        Latest.PitLoss = circuit.PitLoss;
    }
}
