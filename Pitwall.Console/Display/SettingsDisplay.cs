using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// The settings screen: every setting with its current value, changed with the arrow keys and
/// Enter. Changes are saved to config.json straight away.
/// </summary>
public sealed class SettingsDisplay(State state, SettingsStore store) : IDisplay
{
    public Screen Screen => Screen.Settings;

    public Task<IRenderable> GetContentAsync()
    {
        var settings = store.Settings;
        if (state.CursorOffset >= settings.Count)
            state.CursorOffset = settings.Count - 1;
        var selected = settings[state.CursorOffset];

        var table = new Table().NoBorder().NoSafeBorder().HideHeaders().Expand();
        table.AddColumn(new TableColumn("") { Width = 26, NoWrap = true });
        // The value wraps rather than the label, so labels keep their column on narrow screens
        table.AddColumn(new TableColumn(""));

        foreach (var setting in settings)
        {
            var isSelected = setting == selected;
            var label = Markup.Escape(setting.Label);
            var pending = store.Pending(setting);
            var value = Markup.Escape(store.Value(setting));
            var valueMarkup = value.Length == 0 ? "[grey]not set[/]" : $"[bold]{value}[/]";
            if (pending is not null)
                valueMarkup +=
                    $" [grey]→[/] [bold #ffe600]{Markup.Escape(pending)}[/] [grey](after restart)[/]";
            else if (!setting.AppliesNow)
                valueMarkup += " [grey]· changes apply after a restart[/]";

            table.AddRow(
                new Markup(
                    isSelected ? $"[#e10600]▌[/][bold black on white] {label} [/]" : $"  {label}"
                ),
                new Markup(valueMarkup)
            );
        }

        var rows = new List<IRenderable>
        {
            table,
            new Text(""),
            new Markup($"[grey]{Markup.Escape(selected.Help)}[/]"),
        };

        if (store.OverriddenBy(selected) is { } source)
        {
            rows.Add(
                new Markup(
                    $"[yellow]Set by {Markup.Escape(source)}, which wins over the value saved here.[/]"
                )
            );
        }

        // The text box, while typing a folder or address
        if (state.TextInput is { } input)
        {
            rows.Add(new Text(""));
            rows.Add(
                new Panel(new Markup($"{Markup.Escape(input.Text)}[blink]▏[/]"))
                {
                    Header = new PanelHeader($" {Markup.Escape(selected.Label)} "),
                    Expand = true,
                    BorderStyle = new Style(foreground: Color.White),
                }
            );
            if (input.Error is { } error)
                rows.Add(new Markup($"[red]{Markup.Escape(error)}[/]"));
        }

        rows.Add(new Text(""));
        rows.Add(new Markup($"[grey]Saved to {Markup.Escape(ConsoleOptions.ConfigFilePath)}[/]"));

        return Task.FromResult<IRenderable>(
            new Panel(new Rows(rows))
            {
                Header = new PanelHeader(
                    "[bold] SETTINGS [/][grey]▲/▼ move · Enter change · Esc back [/]"
                ),
                Expand = true,
                Padding = new Padding(1, 0),
                BorderStyle = new Style(foreground: PitWallTheme.Grid),
            }
        );
    }
}
