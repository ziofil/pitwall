using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

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
                        "[yellow]Waiting for the session's teams…[/] If no session is running, press [bold]Esc[/] and choose [bold]Watch a live session[/] or [bold]Replay a session[/]."
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

        // A blank row between teams when there's room (two rows go to the border), and full
        // names when they fit
        return Task.FromResult<IRenderable>(
            new Responsive(
                (width, height) =>
                    new Panel(
                        TeamTable(
                            teams,
                            spaced: height is null || height >= (teams.Count * 2) + 1,
                            fullNames: width >= 100
                        )
                    )
                    {
                        Header = new PanelHeader("Select a team for the pit wall (▲/▼, Enter)"),
                        Expand = true,
                    }
            )
        );
    }

    private Table TeamTable(IReadOnlyList<PitWallTeam> teams, bool spaced, bool fullNames)
    {
        var table = new Table().NoBorder().NoSafeBorder().HideHeaders();
        table.AddColumns(
            new TableColumn("") { Width = 2 },
            new TableColumn("Team") { Width = 22, NoWrap = true },
            new TableColumn("Cars") { NoWrap = true }
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
                    var fullName = fullNames
                        ? $" [grey]{Markup.Escape(driver.FullName ?? ""),-22}[/]"
                        : "";
                    return $"[grey]P{line?.Line, -2}[/] [bold]{driver.RacingNumber, 2} {driver.Tla}[/]{fullName}";
                })
            );

            table.AddRow(
                new Markup($"[#{hex} on #{hex}]██[/]"),
                new Markup(selected ? $"[black on #{hex}] {name} [/]" : $"[bold #{hex}] {name}[/]"),
                new Markup(cars)
            );
            if (spaced && i < teams.Count - 1)
                table.AddEmptyRow();
        }
        return table;
    }
}
