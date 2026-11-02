namespace Pitwall.Data;

public class DriverListProcessor : IProcessor<DriverListDataPoint>
{
    public DriverListDataPoint Latest { get; private set; } = new();

    public virtual void Process(DriverListDataPoint data) => Latest.MergeWith(data);
}
