namespace Pitwall.Data;

public static class TimingAppDataPointExtensions
{
    public static Dictionary<string, TimingAppDataPoint.Driver> GetOrderedLines(
        this TimingAppDataPoint data
    ) => data.Lines.OrderBy(x => x.Value.Line).ToDictionary(x => x.Key, x => x.Value);

    /// <summary>
    /// Calculates how many laps the stint lasted
    /// </summary>
    public static int GetStintDuration(this TimingAppDataPoint.Driver.Stint stint) =>
        (stint.TotalLaps - stint.StartLaps) ?? 0;
}
