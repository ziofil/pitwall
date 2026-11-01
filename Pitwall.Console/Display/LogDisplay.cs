using InMemLogger;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public record LogDisplayOptions
{
    public LogLevel MinimumLogLevel = LogLevel.Information;
}

public class LogDisplay(State state, InMemoryLogger inMemoryLogger, LogDisplayOptions options)
    : IDisplay
{
    public Screen Screen => Screen.Logs;

    public Task<IRenderable> GetContentAsync()
    {
        var logs = inMemoryLogger
            .RecordedLogs.ToList()
            .Where(x => x.Level >= options.MinimumLogLevel)
            .Reverse()
            .Select(x =>
                $"{GetLevel(x.Level)} {Markup.Escape(x.Message)} {Markup.Escape(x.Exception?.ToString() ?? string.Empty)}"
            )
            .Skip(state.CursorOffset)
            .Take(20)
            .Select(x => new Markup(x));

        var rowTexts = new List<IRenderable>()
        {
            state.CursorOffset > 0
                ? new Text(
                    $"Skipping {state.CursorOffset} messages",
                    new Style(foreground: Color.Red)
                )
                : new Text($"Minimum Log Level: {options.MinimumLogLevel}"),
        };
        rowTexts.AddRange(logs);
        var rows = new Rows(rowTexts);
        return Task.FromResult<IRenderable>(new Panel(rows).Expand());
    }

    private string GetLevel(LogLevel level) =>
        level switch
        {
            LogLevel.Critical => "[red bold]CRT[/]",
            LogLevel.Error => "[red]ERR[/]",
            LogLevel.Warning => "[yellow]WRN[/]",
            LogLevel.Information => "[green]INF[/]",
            LogLevel.Debug => "[blue]DBG[/]",
            LogLevel.Trace => "[blue]TRC[/]",
            _ => "UNK",
        };
}
