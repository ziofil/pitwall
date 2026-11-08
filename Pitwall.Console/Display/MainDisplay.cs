using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public class MainDisplay(
    MainMenu menu,
    PitWallContext context,
    SessionInfoProcessor sessionInfo,
    IHttpClientFactory httpClientFactory,
    Formula1Account accountService,
    ILogger<MainDisplay> logger
) : IDisplay
{
    public Screen Screen => Screen.Main;

    private Task<string?>? _fetchVersionTask = null;

    private static readonly string[] Logo =
    [
        "██████╗ ██╗████████╗██╗    ██╗ █████╗ ██╗     ██╗     ",
        "██╔══██╗██║╚══██╔══╝██║    ██║██╔══██╗██║     ██║     ",
        "██████╔╝██║   ██║   ██║ █╗ ██║███████║██║     ██║     ",
        "██╔═══╝ ██║   ██║   ██║███╗██║██╔══██║██║     ██║     ",
        "██║     ██║   ██║   ╚███╔███╔╝██║  ██║███████╗███████╗",
        "╚═╝     ╚═╝   ╚═╝    ╚══╝╚══╝ ╚═╝  ╚═╝╚══════╝╚══════╝",
    ];

    // Top to bottom, from racing red to white
    private static readonly string[] LogoColours =
    [
        "#e10600",
        "#ea3a2f",
        "#f2705f",
        "#f8a497",
        "#fcd2cb",
        "#8a8a96",
    ];

    public Task<IRenderable> GetContentAsync()
    {
        var logo = new Rows(
            Logo.Select(
                    (line, i) => (IRenderable)new Markup($"[{LogoColours[i]}]{line}[/]").Centered()
                )
                .Append(new Text(""))
                .Append(new Markup("[bold white]THE TEAM VIEW OF LIVE F1 TIMING[/]").Centered())
                .Append(new Markup($"[grey]{Chequer(40)}[/]").Centered())
        );

        var menuPanel = MenuPanel();
        var statusPanel = StatusPanel();
        var body = new Layout("Body").SplitColumns(
            new Layout("Menu", menuPanel).Ratio(3),
            new Layout("Status", statusPanel).Ratio(2)
        );

        // The version, the update notice when there is one, then the credits that fit
        var update = NewerVersion() is { } newer
            ? $"[green]Pitwall {Markup.Escape(newer)} is out: github.com/ziofil/pitwall/releases[/]"
            : null;
        string[] credits =
        [
            $"v{ThisAssembly.NuGetPackageVersion}",
            .. update is null ? new[] { "github.com/ziofil/pitwall" } : [],
            "a fork of undercut-f1 by Aman Dhoot",
            "unofficial, not associated with Formula 1",
        ];

        var content = new Responsive(
            (width, height) =>
            {
                // Size the panels to their content at this width (the status wraps more on
                // narrow screens), leaving any spare height as space below. On short terminals
                // the logo gives way to a one-line title so the panels keep their height
                var menuWidth = width * 3 / 5;
                var bodyHeight = Math.Max(
                    Lines(menuPanel, menuWidth),
                    Lines(statusPanel, width - menuWidth)
                );
                var footerParts = new List<string>();
                foreach (var part in credits)
                {
                    var candidate = footerParts.Append(part).Prepend(update ?? "");
                    if (Markup.Remove(string.Join(" · ", candidate)).Length > width - 2)
                        break;
                    footerParts.Add(part);
                }
                var footer = new Markup(
                    string.Join(
                        "[grey] · [/]",
                        footerParts
                            .Select(x => $"[grey]{Markup.Escape(x)}[/]")
                            .Prepend(update ?? "")
                            .Where(x => x.Length > 0)
                    )
                ).Centered();

                var compact = height is { } rows && rows < Logo.Length + 3 + bodyHeight + 2;
                var title = compact
                    ? (IRenderable)
                        new Markup(
                            "[bold #e10600]PITWALL[/] [grey]·[/] [bold white]THE TEAM VIEW OF LIVE F1 TIMING[/]"
                        ).Centered()
                    : logo;
                return new Layout("Main").SplitRows(
                    new Layout("Logo", title).Size(compact ? 1 : Logo.Length + 3),
                    new Layout("Gap", new Text("")).Size(1),
                    new Layout("Body", body).Size(bodyHeight),
                    new Layout("Footer", footer).Size(1),
                    new Layout("Spare", new Text(""))
                );
            }
        );

        return Task.FromResult<IRenderable>(content);
    }

    private IRenderable MenuPanel()
    {
        var items = menu.Items;
        var selected = menu.SelectedIndex;
        var rows = new List<IRenderable>();
        for (var i = 0; i < items.Count; i++)
        {
            var label = Markup.Escape(items[i].Label);
            rows.Add(
                i == selected
                    ? new Markup($"[#e10600]▌[/][bold black on white] {label} [/]")
                    : new Markup($"  [white]{label}[/]")
            );
        }
        rows.Add(new Text(""));
        rows.Add(new Markup($"[grey]{Markup.Escape(items[selected].Detail)}[/]"));

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold] MENU [/][grey]▲/▼ move · Enter open [/]"),
            Expand = true,
            Padding = new Padding(1, 0),
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }

    private IRenderable StatusPanel()
    {
        // Labels in their own column, so values that wrap stay lined up
        var grid = new Grid().AddColumn(new GridColumn { Width = 7, NoWrap = true }).AddColumn();
        void Row(string label, string value) =>
            grid.AddRow(new Markup($"[grey]{label}[/]"), new Markup(value));

        if (menu.SessionRunning)
        {
            var session = string.Join(
                " · ",
                new[] { sessionInfo.Latest.Meeting?.Name, sessionInfo.Latest.Name }.Where(x =>
                    !string.IsNullOrWhiteSpace(x)
                )
            );
            Row(
                "SESSION",
                (session.Length == 0 ? "[bold]starting…[/]" : $"[bold]{Markup.Escape(session)}[/]")
                    + "\n[grey]restart Pitwall to watch a different session[/]"
            );
        }
        else
        {
            Row("SESSION", "none running yet");
        }

        // The pit wall follows the leading team until another is picked
        var team = menu.SessionRunning ? context.Team : null;
        Row(
            "TEAM",
            team is null
                ? "[grey]leading team · ▲ ▼ to change[/]"
                : $"[{PitWallTheme.TeamColour(team.Colour).ToMarkup()}]■[/] [bold]{Markup.Escape(team.Name)}[/]"
        );

        Row(
            "F1 TV",
            accountService.IsAuthenticated switch
            {
                Formula1Account.AuthenticationResult.Success =>
                    $"[green]logged in[/] [grey]until {accountService.Payload!.Expiry:yyyy-MM-dd}[/]",
                Formula1Account.AuthenticationResult.ExpiredToken =>
                    "[yellow]login expired[/] [grey]· see F1 TV account[/]",
                _ =>
                    "[yellow]not logged in[/] [grey](needed for live telemetry)[/]",
            }
        );

        var keys = new Grid().AddColumn(new GridColumn { Width = 5, NoWrap = true }).AddColumn();
        foreach (
            var (key, action) in new[]
            {
                ("◄ ►", "change page (or 1–6)"),
                ("▲ ▼", "change team (also Tab)"),
                ("Enter", "pick from the team list"),
                ("Esc", "back to this menu"),
                ("Space", "sync to TV at the start"),
                ("D", "stream delay"),
                ("P", "pause the clock"),
            }
        )
        {
            keys.AddRow(new Markup($"[white]{key}[/]"), new Markup($"[grey]{action}[/]"));
        }

        var rows = new List<IRenderable>
        {
            grid,
            new Text(""),
            new Markup("[grey]On the pit wall[/]"),
            keys,
        };

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold] STATUS [/]"),
            Expand = true,
            Padding = new Padding(1, 0),
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }

    private static string Chequer(int width) =>
        string.Concat(Enumerable.Range(0, width).Select(i => i % 2 == 0 ? "▀" : "▄"));

    /// <summary>
    /// The latest release on GitHub if it's newer than this one, else <c>null</c>. Fetched in the
    /// background, so this is <c>null</c> until the answer is in (or if it can't be fetched).
    /// </summary>
    private string? NewerVersion()
    {
        _fetchVersionTask ??= Task.Run(GetLatestVersionAsync);
        return
            _fetchVersionTask is { IsCompletedSuccessfully: true, Result: { } latest }
            && Version.TryParse(latest.TrimStart('v'), out var latestVersion)
            && Version.TryParse(ThisAssembly.NuGetPackageVersion, out var current)
            && latestVersion > current
            ? latest
            : null;
    }

    /// <summary>How many lines a renderable takes at a width.</summary>
    private static int Lines(IRenderable renderable, int width) =>
        Segment
            .SplitLines(
                renderable.Render(
                    new RenderOptions(
                        AnsiConsole.Console.Profile.Capabilities,
                        new Size(width, 1000)
                    ),
                    width
                )
            )
            .Count;

    private async Task<string?> GetLatestVersionAsync()
    {
        // Not the "Default" client: that one carries the F1 account token
        var httpClient = httpClientFactory.CreateClient("Public");
        var res = await httpClient.GetFromJsonAsync(
            "https://api.github.com/repos/ziofil/pitwall/tags",
            ConsoleSerializerContext.Pretty.GitHubTagEntryArray
        );
        var tag = res?.FirstOrDefault()?.Name;
        logger.LogInformation("Latest tag from GitHub: {Tag}", tag);
        return tag;
    }

    internal record GitHubTagEntry(string Name);
}
