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
    private string _currentVersion = $"v{ThisAssembly.NuGetPackageVersion}";

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

        var body = new Layout("Body").SplitColumns(
            new Layout("Menu", MenuPanel()).Ratio(3),
            new Layout("Status", StatusPanel()).Ratio(2)
        );

        var latestVersion = GetLatestVersion();
        var newVersionText =
            latestVersion == _currentVersion
                ? string.Empty
                : $"  [green]A newer version is available: {latestVersion}[/]";
        var footer = new Markup(
            $"[grey]{ThisAssembly.AssemblyInformationalVersion} · github.com/ziofil/pitwall · a fork of undercut-f1 by Aman Dhoot · unofficial, not associated with Formula 1[/]{newVersionText}"
        ).Centered();

        // Size the panels to the menu (entries, a blank line, the detail line and the border),
        // leaving any spare height as space below
        var bodyHeight = Math.Max(menu.Items.Count + 4, 13);
        var layout = new Layout("Main").SplitRows(
            new Layout("Logo", logo).Size(Logo.Length + 3),
            new Layout("Gap", new Text("")).Size(1),
            new Layout("Body", body).Size(bodyHeight),
            new Layout("Footer", footer).Size(1),
            new Layout("Spare", new Text(""))
        );

        return Task.FromResult<IRenderable>(layout);
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
        var rows = new List<IRenderable>();

        if (menu.SessionRunning)
        {
            var session = $"{sessionInfo.Latest.Meeting?.Name} · {sessionInfo.Latest.Name}";
            rows.Add(new Markup($"[grey]SESSION[/]  [bold]{Markup.Escape(session)}[/]"));
        }
        else
        {
            rows.Add(new Markup("[grey]SESSION[/]  none running yet"));
        }

        var team = context.Team;
        rows.Add(
            team is null
                ? new Markup("[grey]TEAM   [/]  none selected")
                : new Markup(
                    $"[grey]TEAM   [/]  [{PitWallTheme.TeamColour(team.Colour).ToMarkup()}]■[/] [bold]{Markup.Escape(team.Name)}[/]"
                )
        );

        rows.Add(
            accountService.IsAuthenticated switch
            {
                Formula1Account.AuthenticationResult.Success => new Markup(
                    $"[grey]F1 TV  [/]  [green]logged in[/] [grey]until {accountService.Payload!.Expiry:yyyy-MM-dd}[/]"
                ),
                Formula1Account.AuthenticationResult.ExpiredToken => new Markup(
                    "[grey]F1 TV  [/]  [yellow]login expired[/] [grey]· see F1 TV account[/]"
                ),
                _ => new Markup(
                    "[grey]F1 TV  [/]  [yellow]not logged in[/] [grey](needed for live telemetry)[/]"
                ),
            }
        );

        rows.Add(new Text(""));
        rows.Add(
            new Markup(
                """
                [grey]On the pit wall[/]
                [white]◄ ►[/]  [grey]change page[/]
                [white]▲ ▼[/]  [grey]change team (also Tab / Shift+Tab)[/]
                [white]Enter[/] [grey]pick from the team list[/]
                [white]Esc[/]   [grey]back to this menu[/]
                [white]N M[/]   [grey]stream delay · [/][white]P[/] [grey]pause[/]
                """
            )
        );

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

    private string GetLatestVersion()
    {
        // We don't want to block anything when fetching the latest version from GitHub
        // So we trigger a background task to fetch, and return the current version until we have a result available.
        switch (_fetchVersionTask)
        {
            case null:
                _fetchVersionTask = Task.Run(GetLatestVersionAsync);
                return _currentVersion;
            case { IsCompletedSuccessfully: true }:
                return _fetchVersionTask.Result ?? _currentVersion;
            default:
                return _currentVersion;
        }
    }

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
