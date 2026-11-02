using Spectre.Console;
using Spectre.Console.Rendering;
using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>Lists the teams in the session so one can be picked for the pit wall pages.</summary>
public sealed class SelectTeamDisplay(
    State state,
    PitWallContext context,
    TimingDataProcessor timingData
) : IDisplay
{
    public Screen Screen => Screen.SelectTeam;

    public Task<IRenderable> GetContentAsync()
    {
        var teams = context.Teams;
        if (teams.Count == 0)
        {
            return Task.FromResult<IRenderable>(
                new Panel(
                    new Markup(
                        "[yellow]No teams found yet.[/] Start a live or recorded session first (press [bold]Esc[/], then [bold]S[/])."
                    )
                )
                {
                    Header = new PanelHeader("Select Team"),
                    Expand = true,
                }
            );
        }

        if (state.CursorOffset >= teams.Count)
            state.CursorOffset = teams.Count - 1;

        var table = new Table().NoBorder().NoSafeBorder().HideHeaders();
        table.AddColumns(
            new TableColumn("") { Width = 2 },
            new TableColumn("Team") { Width = 26 },
            new TableColumn("Cars")
        );

        for (var i = 0; i < teams.Count; i++)
        {
            var team = teams[i];
            var hex = PitWallTheme.TeamColour(team.Colour).ToHex();
            var selected = i == state.CursorOffset;
            var name = Markup.Escape(team.Name.ToUpperInvariant());
            var cars = string.Join(
                "   ",
                team.DriverNumbers.Select(number =>
                {
                    var driver = context.Driver(number);
                    var line = timingData.Latest.Lines.GetValueOrDefault(number);
                    return $"P{line?.Line,-2} [bold]{driver.RacingNumber,2} {driver.Tla}[/] [grey]{Markup.Escape(driver.FullName ?? "")}[/]";
                })
            );

            table.AddRow(
                new Markup($"[#{hex} on #{hex}]██[/]"),
                new Markup(selected ? $"[black on #{hex}] {name} [/]" : $"[bold #{hex}] {name}[/]"),
                new Markup(cars)
            );
            table.AddEmptyRow();
        }

        return Task.FromResult<IRenderable>(
            new Panel(table)
            {
                Header = new PanelHeader("Select a team for the pit wall (▲/▼, Enter)"),
                Expand = true,
            }
        );
    }
}
