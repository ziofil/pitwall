using System.Text.RegularExpressions;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// The communications page: race control messages coloured as on the team screens (yellow for
/// flags, white for clears, magenta for incidents and investigations, red for red flags and safety
/// cars), with messages about the team's cars marked, alongside the team's own radio channels.
/// </summary>
public sealed partial class PitWallCommsDisplay(
    State state,
    PitWallComponents components,
    PitWallContext context,
    RaceControlMessageProcessor raceControl,
    TeamRadioProcessor teamRadio,
    SessionInfoProcessor sessionInfo,
    IDateTimeProvider dateTimeProvider,
    RadioTranscriber transcriber
) : IDisplay
{
    private const string Led = "#ff2020";

    public Screen Screen => Screen.PitWallComms;

    /// <summary>
    /// The team radio captures for the selected team, newest first. The cursor on this page
    /// indexes into this list, which is also what the play and transcribe keys act on.
    /// </summary>
    public static List<KeyValuePair<string, TeamRadioDataPoint.Capture>> TeamCaptures(
        TeamRadioProcessor teamRadio,
        PitWallContext context
    ) =>
        teamRadio
            .Ordered.Where(x =>
                x.Value.RacingNumber is not null && context.IsTeamCar(x.Value.RacingNumber)
            )
            .ToList();

    public Task<IRenderable> GetContentAsync()
    {
        var body = new Layout("Comms").SplitColumns(
            new Layout("RaceControl", RaceControlPanel()).Ratio(3),
            new Layout("Radio", RadioPanel()).Ratio(2)
        );
        return Task.FromResult(components.Page(Screen, body));
    }

    /// <summary>The colour a race control message is shown in.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style",
        "IDE0046:Convert to conditional expression",
        Justification = "Harder to read"
    )]
    public static Style MessageStyle(RaceControlMessageDataPoint.RaceControlMessage message)
    {
        var text = message.Message?.ToUpperInvariant() ?? "";
        var flag = message.Flag?.ToUpperInvariant();

        if (flag == "RED" || message.Category == "SafetyCar" || text.Contains("SAFETY CAR"))
            return new Style(
                foreground: Color.White,
                background: new Color(200, 0, 0),
                decoration: Decoration.Bold
            );
        if (flag is "YELLOW" or "DOUBLE YELLOW")
            return new Style(foreground: new Color(255, 255, 0));
        if (flag == "BLUE")
            return new Style(foreground: new Color(80, 160, 255));
        if (flag == "CHEQUERED")
            return new Style(foreground: Color.White, decoration: Decoration.Bold);
        if (
            text.Contains("INVESTIGAT")
            || text.Contains("PENALTY")
            || text.Contains("NOTED")
            || text.Contains("DELETED")
            || text.Contains("REPRIMAND")
            || text.Contains("STOPPED")
        )
            return new Style(foreground: new Color(255, 51, 255));
        return flag is "CLEAR" or "GREEN"
            ? PitWallTheme.Normal
            : new Style(foreground: new Color(200, 200, 210));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"CARS? (\d+)")]
    private static partial Regex CarNumbersRegex();

    private bool IsAboutTeam(RaceControlMessageDataPoint.RaceControlMessage message)
    {
        if (message.RacingNumber is not null && context.IsTeamCar(message.RacingNumber))
            return true;
        var text = message.Message ?? "";
        return CarNumbersRegex().Matches(text).Any(m => context.IsTeamCar(m.Groups[1].Value))
            || (context.Team?.DriverNumbers ?? []).Any(n =>
                text.Contains($"({context.Driver(n).Tla})", StringComparison.OrdinalIgnoreCase)
            );
    }

    private IRenderable RaceControlPanel()
    {
        var table = new Table().NoBorder().NoSafeBorder().HideHeaders().Expand();
        table.AddColumn(new TableColumn("") { Width = 1, Padding = new Padding(0) });
        table.AddColumn(new TableColumn("") { Width = 8, Padding = new Padding(0, 0, 1, 0) });
        table.AddColumn(new TableColumn("") { Width = 6, Padding = new Padding(0, 0, 1, 0) });
        table.AddColumn(new TableColumn(""));

        var teamHex = PitWallTheme.TeamColour(context.Team?.Colour).ToHex();
        var offset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);

        foreach (
            var message in raceControl.Latest.Messages.Values.OrderByDescending(x => x.Utc).Take(60)
        )
        {
            var aboutTeam = IsAboutTeam(message);
            var style = MessageStyle(message);
            if (aboutTeam)
                style = style.Combine(new Style(decoration: Decoration.Bold));

            table.AddRow(
                new Markup(aboutTeam ? $"[#{teamHex}]▌[/]" : " "),
                // The feed's timestamps are UTC but carry no offset, so they parse as local time
                new Text(
                    $@"{new DateTimeOffset(message.Utc.DateTime, TimeSpan.Zero).ToOffset(offset):HH\:mm\:ss}",
                    PitWallTheme.Dim
                ),
                new Text(message.Lap is null ? "" : $"L{message.Lap}", PitWallTheme.Dim),
                new Text(message.Message?.ToUpperInvariant() ?? "", style)
            );
        }

        return new Panel(table)
        {
            Header = new PanelHeader(
                $"[bold] RACE CONTROL [/][grey]newest first · [/][#{teamHex}]▌[/][grey] about {Markup.Escape(context.Team?.Name ?? "team")} [/]"
            ),
            Expand = true,
            Padding = new Padding(0),
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }

    private IRenderable RadioPanel()
    {
        var team = context.Team;
        var captures = TeamCaptures(teamRadio, context);
        if (state.CursorOffset >= captures.Count)
            state.CursorOffset = Math.Max(0, captures.Count - 1);
        var selected = captures.ElementAtOrDefault(state.CursorOffset);

        var rows = new List<IRenderable>();

        // Channel keys, like the radio panels under the pit wall screens: lit when that car has
        // had a message in the last 30 seconds
        var channels = (team?.DriverNumbers ?? []).Select(number =>
        {
            var driver = context.Driver(number);
            var latest = captures.FirstOrDefault(x => x.Value.RacingNumber == number).Value;
            var live =
                latest?.Utc is { } utc && dateTimeProvider.Utc - utc < TimeSpan.FromSeconds(30);
            return live
                ? $"[bold black on {Led}] {driver.RacingNumber} {driver.Tla} [/]"
                : $"[bold {Led} on #200000] {driver.RacingNumber} {driver.Tla} [/]";
        });
        rows.Add(new Markup(string.Join("  ", channels)));

        // Transcription runs by itself in the background once the model is on this machine
        if (transcriber.Problem is { } problem)
            rows.Add(new Markup($"[yellow]{Markup.Escape(problem)}[/]"));
        else if (!transcriber.ModelAvailable)
            rows.Add(
                new Markup(
                    "[grey]To transcribe messages, download the transcription model from the menu (one time, 550 MB, runs on this machine).[/]"
                )
            );
        rows.Add(new Text(""));

        if (captures.Count == 0)
        {
            rows.Add(new Text("NO RADIO MESSAGES YET", PitWallTheme.Dim));
        }

        var offset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);

        // A table, so long transcriptions wrap under their own column. The selected message is
        // shown in full, the others cut to about two lines.
        var list = new Table().NoBorder().NoSafeBorder().HideHeaders().Expand();
        list.AddColumn(new TableColumn("") { Width = 1, Padding = new Padding(0, 0, 1, 0) });
        list.AddColumn(new TableColumn("") { Width = 8, Padding = new Padding(0, 0, 1, 0) });
        list.AddColumn(new TableColumn("") { Width = 3, Padding = new Padding(0, 0, 1, 0) });
        list.AddColumn(new TableColumn(""));

        foreach (var (key, capture) in captures.Take(20))
        {
            var driver = context.Driver(capture.RacingNumber);
            var isSelected = key == selected.Key;
            // Whisper leaves extra spaces between sentences; show the text as one clean paragraph
            var transcription = capture.Transcription is null
                ? null
                : Whitespace().Replace(capture.Transcription, " ").Trim();
            if (!isSelected && transcription?.Length > 110)
            {
                // Cut at the last word that fits, or mid-word if there's no space at all
                var cut = transcription.LastIndexOf(' ', 110);
                transcription = transcription[..(cut > 0 ? cut : 110)].TrimEnd() + "…";
            }
            var text = transcription is not null
                ? isSelected
                    ? $"[bold]{Markup.Escape(transcription)}[/]"
                    : Markup.Escape(transcription)
                : transcriber.Current == key
                    ? "[olive]transcribing…[/]"
                    : transcriber.ModelAvailable && transcriber.Problem is null
                        ? "[grey]waiting to transcribe[/]"
                        : "[grey]Enter to play[/]";

            list.AddRow(
                new Markup(isSelected ? "[black on white]►[/]" : " "),
                new Markup($@"[grey]{capture.Utc?.ToOffset(offset):HH\:mm\:ss}[/]"),
                new Markup($"[bold {Led}]{driver.Tla}[/]"),
                new Markup(text)
            );
        }
        rows.Add(list);

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold] TEAM RADIO [/][grey]▲/▼ select · Enter play [/]"),
            Expand = true,
            BorderStyle = new Style(foreground: PitWallTheme.Grid),
        };
    }
}
