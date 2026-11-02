using System.Text.Json;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public sealed class DebugDataDisplay(IEnumerable<IProcessor> processors, State state) : IDisplay
{
    public Screen Screen => Screen.DebugData;

    public Task<IRenderable> GetContentAsync()
    {
        var processor = processors.ElementAtOrDefault(state.CursorOffset);
        if (processor is null)
            return Task.FromResult<IRenderable>(
                new Text($"No processor for {state.CursorOffset} cursor offset")
            );
        var processorName = processor.GetType().Name;

        var latestDataPoint = processor.GetLatestData();
        if (latestDataPoint is null)
            return Task.FromResult<IRenderable>(
                new Text($"Latest datapoint for {processorName} is null")
            );

        var rows = new Rows(
            new Text($"Name: {processorName}"),
            new Text(
                JsonSerializer.Serialize(
                    latestDataPoint,
                    TimingDataSerializerContext.Pretty.GetTypeInfo(latestDataPoint.GetType())!
                )
            )
        );

        return Task.FromResult<IRenderable>(rows);
    }
}
