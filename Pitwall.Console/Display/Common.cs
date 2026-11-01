using Spectre.Console;
using Spectre.Console.Rendering;
using Pitwall.Data;

namespace Pitwall.Console;

public sealed class CommonDisplayComponents(
    ExtrapolatedClockProcessor extrapolatedClock,
    TrackStatusProcessor trackStatus,
    SessionInfoProcessor sessionInfo,
    IDateTimeProvider dateTimeProvider
)
{
    public IRenderable GetStatusPanel()
    {
        var items = new List<IRenderable>();

        if (trackStatus.Latest is not null)
        {
            var style = trackStatus.Latest.Status switch
            {
                "1" => DisplayUtils.STYLE_PB, // All Clear
                "2" => new Style(foreground: Color.Black, background: Color.Yellow), // Yellow Flag
                "4" => new Style(foreground: Color.Black, background: Color.Yellow), // Safety Car
                "6" => new Style(foreground: Color.Black, background: Color.Yellow), // VSC Deployed
                "7" => new Style(foreground: Color.Black, background: Color.Yellow), // VSC Ending
                "5" => new Style(foreground: Color.White, background: Color.Red), // Red Flag
                _ => Style.Plain,
            };
            items.Add(new Text($"{trackStatus.Latest.Message}", style));
        }

        var localOffset = string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
            ? TimeSpan.Zero
            : TimeSpan.Parse(sessionInfo.Latest.GmtOffset);
        var localDate = dateTimeProvider.Utc.ToOffset(localOffset);

        items.Add(new Text($@"{localDate:HH\:mm\:ss}"));
        items.Add(new Text($@"{extrapolatedClock.ExtrapolatedRemaining():hh\:mm\:ss}"));

        var rows = new Rows(items);
        return new Panel(rows)
        {
            Header = new PanelHeader("Status"),
            Expand = true,
            Border = BoxBorder.Rounded,
        };
    }
}
