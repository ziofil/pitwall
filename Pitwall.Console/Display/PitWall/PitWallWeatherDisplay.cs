using Pitwall.Console.Graphics;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// The weather page: conditions now and how they've changed over the session, the rain
/// forecast at the circuit, which tyres the field is on and whether wet tyres are faster yet,
/// and the rain radar around the circuit.
/// </summary>
public sealed class PitWallWeatherDisplay(
    PitWallComponents components,
    PitWallContext context,
    WeatherHistoryProcessor history,
    SessionInfoProcessor sessionInfo,
    TimingDataProcessor timingData,
    TimingAppDataProcessor timingAppData,
    RainRadar radar,
    RainForecast forecast,
    IDateTimeProvider dateTimeProvider,
    ITerminalImages images
) : IDisplay
{
    /// <summary>The radar and forecast are for now, so are only shown when the session clock is close to it.</summary>
    private static readonly TimeSpan LiveTolerance = TimeSpan.FromMinutes(30);

    /// <summary>How far back the radar loop goes from the session clock.</summary>
    private static readonly TimeSpan RadarLoop = TimeSpan.FromHours(1);

    private static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(15);

    private static readonly Color TitleBar = new(43, 43, 43);
    private static readonly Color TrackTemp = new(255, 140, 0);
    private static readonly Color AirTemp = new(0, 200, 230);
    private static readonly Color Rain = new(47, 127, 255);

    private const int MinTextColumns = 70;

    private readonly InlineImage _image = new(images);

    /// <summary>How long the loop takes to play from one radar frame (10 minutes) to the next.</summary>
    private static readonly TimeSpan FramePlayTime = TimeSpan.FromSeconds(1.2);

    private static readonly TimeSpan LoopHold = TimeSpan.FromSeconds(2.5);

    // The frame drawn between two radar frames, replaced every redraw
    private SkiaSharp.SKImage? _between;

    public Screen Screen => Screen.PitWallWeather;

    // Plays the radar loop; nothing else on the page changes faster than the weather feed
    public TimeSpan FrameInterval =>
        _image.Supported && _image.FrameInterval < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
        : _image.Supported ? _image.FrameInterval
        : TimeSpan.FromMilliseconds(IDisplay.DefaultFrameIntervalMs);

    public Task PostContentDrawAsync(bool forceDraw) => _image.DrawAsync(forceDraw);

    public Task<IRenderable> GetContentAsync()
    {
        var samples = history.Samples;
        var location = CircuitLocations.Get(sessionInfo.Latest.Meeting?.Circuit?.Key);
        var clock = dateTimeProvider.Utc;
        var live = (DateTimeOffset.UtcNow - clock).Duration() < LiveTolerance;

        // Fixed-height sections, with the history chart taking whatever height is left. On short
        // terminals the blank rows between sections go, so the chart keeps some height
        var bodyHeight = _image.WindowCells.Rows - PitWallComponents.BodyRowsOffset;
        var gap = bodyHeight >= 30 ? 1 : 0;
        Layout Section(string name, IRenderable content, int? rows = null) =>
            rows is null ? new Layout(name, content) : new Layout(name, content).Size(rows.Value);
        List<Layout> sections =
        [
            Section("ConditionsTitle", Title("CONDITIONS"), 1),
            Section("Conditions", Conditions(samples, clock), 4),
            Section("Gap1", new Text(""), gap),
            Section("HistoryTitle", Title("SESSION HISTORY"), 1),
            Section("History", HistoryChart(samples, clock)),
            Section("Gap2", new Text(""), gap),
            Section("OutlookTitle", Title("RAIN OUTLOOK · next 3 hours at the circuit"), 1),
            // Outside live sessions the outlook is only a note
            Section("Outlook", Outlook(location, live), live && location is not null ? 5 : 2),
            Section("Gap3", new Text(""), gap),
            Section("TyresTitle", Title("TYRES"), 1),
            Section("Tyres", Tyres(samples.Count > 0 && samples[^1].Rainfall), 3),
        ];
        if (gap == 0)
            sections.RemoveAll(x => x.Name?.StartsWith("Gap") == true);

        // The radar takes a square on the right, as tall as the page, if the text still fits
        var radarColumns = 0;
        if (_image.Supported && location is { } at)
        {
            var (columns, rows) = _image.WindowCells;
            var bodyRows = rows - PitWallComponents.BodyRowsOffset;
            var (width, height) = _image.PixelSize(columns, Math.Max(1, bodyRows));
            var cellAspect = height / (double)Math.Max(1, bodyRows) / (width / (double)columns);
            radarColumns = (int)Math.Min(columns * 0.5, bodyRows * cellAspect);
            if (columns - radarColumns < MinTextColumns)
                radarColumns = 0;

            if (radarColumns > 0)
            {
                var images = radar.Get(at.Latitude, at.Longitude);
                var scene = RadarScene(images, clock, live);
                _image.Update(
                    PitWallComponents.HeaderHeight,
                    columns - radarColumns,
                    radarColumns,
                    bodyRows,
                    (w, h) => RadarRenderer.Render(scene, w, h)
                );
            }
        }
        if (radarColumns == 0)
            _image.Clear();

        if (radarColumns == 0)
            sections.Add(Section("RadarNote", new Markup(RadarNote(location)), 1));
        var info = new Layout("Info").SplitRows([.. sections]);

        IRenderable body =
            radarColumns > 0
                ? new Layout("Weather").SplitColumns(
                    info,
                    new Layout("Radar", new Text("")).Size(radarColumns)
                )
                : info;

        return Task.FromResult(components.Page(Screen, body));
    }

    private string RadarNote((double Latitude, double Longitude)? location) =>
        location is null ? "[grey]No radar: this circuit's location isn't known.[/]"
        : !_image.Supported
            ? "[grey]The rain radar needs a terminal with inline images (Kitty, iTerm2 or Sixel).[/]"
        : "[grey]Widen the window to see the rain radar.[/]";

    private RadarScene RadarScene(RadarImages images, DateTimeOffset clock, bool live)
    {
        var frames = images
            .Frames.Where(x =>
                x.Time <= clock + TimeSpan.FromMinutes(5) && x.Time >= clock - RadarLoop
            )
            .OrderBy(x => x.Time)
            .ToList();

        var message =
            images.Error is { } error ? error
            : images.Frames.Count == 0 ? "Loading radar…"
            : frames.Count == 0 ? "Radar only covers the last 2 hours"
            : (string?)null;

        // Play through the frames continuously, moving the rain between them, then hold on the
        // newest for a moment
        var loop = (Math.Max(0, frames.Count - 1) * FramePlayTime) + LoopHold;
        var position = Math.Min(
            Environment.TickCount64
                % (long)loop.TotalMilliseconds
                / FramePlayTime.TotalMilliseconds,
            Math.Max(0, frames.Count - 1)
        );
        var index = (int)position;
        var t = (float)(position - index);
        var frame = frames.Count == 0 ? null : frames[index];
        var next = index + 1 < frames.Count ? frames[index + 1] : null;

        _between?.Dispose();
        _between =
            frame is { ToNext: { } flow } && next is not null && t > 0.01f
                ? RadarMotion.Between(frame.Pixels, next.Pixels, flow, t)
                : null;
        var time = frame is null
            ? (DateTimeOffset?)null
            : frame.Time + ((next is null ? TimeSpan.Zero : next.Time - frame.Time) * t);
        var label = time is null
            ? ""
            : $"RADAR {Local(time.Value):HH:mm}"
                + (
                    frames[^1].Time - time.Value < TimeSpan.FromMinutes(1)
                        ? ""
                        : $" · −{(frames[^1].Time - time.Value).TotalMinutes:0} min"
                );

        return new RadarScene(
            images.Map,
            _between ?? frame?.Image,
            label,
            frames.Count == 0 ? 0 : (int)Math.Round(position) + 1,
            frames.Count,
            message,
            live ? null : "Session clock isn't live: radar shown for its time"
        );
    }

    private static SingleLine Title(string text) => new($"[white] {text}[/]", TitleBar);

    private IRenderable Conditions(IReadOnlyList<WeatherSample> samples, DateTimeOffset clock)
    {
        if (samples.Count == 0)
            return new Markup("[grey]No weather data yet.[/]");

        var now = samples[^1];
        var before = samples.LastOrDefault(x => x.Utc <= clock - TrendWindow);
        string Trend(double? value, double? earlier, string unit = "")
        {
            if (value is null || earlier is null || before == default)
                return "";
            var change = value.Value - earlier.Value;
            return Math.Abs(change) < 0.05 ? "[grey]  steady[/]"
                : change > 0 ? $"[#ff8c00]  ▲ {change:0.0}{unit}[/]"
                : $"[#00c8e6]  ▼ {-change:0.0}{unit}[/]";
        }

        var wind = now.WindDirection is { } from
            ? $"[bold]{now.WindSpeed:0.0}[/] m/s from {Compass(from)} [grey]({from}°)[/] {WindArrow(from)}"
            : $"[bold]{now.WindSpeed:0.0}[/] m/s";

        var table = new Table().NoBorder().NoSafeBorder().HideHeaders();
        table.AddColumn(new TableColumn("") { Width = 10, Padding = new Padding(1, 0, 1, 0) });
        table.AddColumn(new TableColumn("") { Padding = new Padding(0, 0, 3, 0) });
        table.AddColumn(new TableColumn("") { Width = 10, Padding = new Padding(0, 0, 1, 0) });
        table.AddColumn(new TableColumn(""));
        table.AddRow(
            new Markup("[grey]TRACK[/]"),
            new Markup(
                $"[bold #ff8c00]{now.TrackTemp:0.0}°C[/]{Trend(now.TrackTemp, before.TrackTemp, "°")}"
            ),
            new Markup("[grey]AIR[/]"),
            new Markup(
                $"[bold #00c8e6]{now.AirTemp:0.0}°C[/]{Trend(now.AirTemp, before.AirTemp, "°")}"
            )
        );
        table.AddRow(
            new Markup("[grey]HUMIDITY[/]"),
            new Markup($"[bold]{now.Humidity:0}%[/]{Trend(now.Humidity, before.Humidity, "%")}"),
            new Markup("[grey]PRESSURE[/]"),
            new Markup($"[bold]{now.Pressure:0.0}[/] hPa{Trend(now.Pressure, before.Pressure)}")
        );
        table.AddRow(
            new Markup("[grey]WIND[/]"),
            new Markup(wind),
            new Markup("[grey]RAIN[/]"),
            new Markup(RainSpell(samples))
        );

        return new Rows(
            table,
            new Markup($"[grey] Changes over the last {TrendWindow.TotalMinutes:0} minutes.[/]")
        );
    }

    /// <summary>Whether it's raining on track, and for how long it has (or hasn't) been.</summary>
    private static string RainSpell(IReadOnlyList<WeatherSample> samples)
    {
        var raining = samples[^1].Rainfall;
        var since = samples.LastOrDefault(x => x.Rainfall != raining);
        var duration = since == default ? null : (TimeSpan?)(samples[^1].Utc - since.Utc);
        var text = duration is null ? "all session" : $"for {Duration(duration.Value)}";
        return raining
            ? $"[bold white on #2f7fff] RAINING [/] [grey]{text}[/]"
            : $"[bold]DRY[/] [grey]{text}[/]";
    }

    private IRenderable HistoryChart(IReadOnlyList<WeatherSample> samples, DateTimeOffset clock)
    {
        if (samples.Count < 2)
            return new Markup("[grey]Builds up as weather updates arrive, about once a minute.[/]");

        var start = samples[0].Utc;
        var end = clock > samples[^1].Utc ? clock : samples[^1].Utc;
        var span = Math.Max(60, (end - start).TotalSeconds);
        var temps = samples
            .SelectMany(x => new[] { x.TrackTemp, x.AirTemp })
            .OfType<double>()
            .DefaultIfEmpty(0)
            .ToList();
        var min = Math.Floor(temps.Min()) - 1;
        var max = Math.Ceiling(temps.Max()) + 1;

        var chart = new CanvasRenderable(
            canvas =>
            {
                double X(DateTimeOffset utc) => (utc - start).TotalSeconds;
                int Column(DateTimeOffset utc) =>
                    (int)Math.Round(X(utc) / span * (canvas.Width - 1));
                canvas.HorizontalGuide(canvas.Height - 1, PitWallTheme.Grid);
                // Rain on track as a strip along the bottom, from each wet update to the next
                for (var i = 0; i < samples.Count; i++)
                {
                    if (!samples[i].Rainfall)
                        continue;
                    var from = Column(samples[i].Utc);
                    var to = Column(i + 1 < samples.Count ? samples[i + 1].Utc : end);
                    canvas.Line(from, canvas.Height - 1, to, canvas.Height - 1, Rain);
                    canvas.Line(from, canvas.Height - 2, to, canvas.Height - 2, Rain);
                }
                canvas.Plot(
                    samples
                        .Where(x => x.AirTemp is not null)
                        .Select(x => (X(x.Utc), x.AirTemp!.Value)),
                    0,
                    span,
                    min,
                    max,
                    AirTemp
                );
                canvas.Plot(
                    samples
                        .Where(x => x.TrackTemp is not null)
                        .Select(x => (X(x.Utc), x.TrackTemp!.Value)),
                    0,
                    span,
                    min,
                    max,
                    TrackTemp
                );
                canvas.Label(0, 0, $"{max:0}°", PitWallTheme.Dim);
                canvas.Label(0, canvas.Rows - 1, $"{min:0}°", PitWallTheme.Dim);
            },
            8
        );

        return new Layout("HistoryChart").SplitRows(
            new Layout("Chart", chart),
            new Layout(
                "Legend",
                new SingleLine(
                    $"[grey] {Local(start):HH:mm}[/]  [#ff8c00]━ track[/]  [#00c8e6]━ air[/]  [#2f7fff]▁ rain[/]  [grey]to {Local(end):HH:mm}[/]"
                )
            ).Size(1)
        );
    }

    private IRenderable Outlook((double Latitude, double Longitude)? location, bool live)
    {
        if (location is not { } at)
            return new Markup(" [grey]No forecast: this circuit's location isn't known.[/]");
        if (!live)
            return new Markup(
                " [grey]Live sessions only: the forecast is for now, not for the session being replayed.[/]"
            );

        var radarLine = new SingleLine($" [grey]RADAR[/]  {RadarOutlook(at)}");
        var slots = forecast.Get(at.Latitude, at.Longitude);
        if (slots.Count == 0)
            return new Rows(radarLine, new Markup(" [grey]MODEL  Loading forecast…[/]"));

        static string Colour(RainSlot slot) =>
            slot.Probability >= 60 || slot.Millimetres >= 0.5 ? "#2f7fff"
            : slot.Probability >= 30 || slot.Millimetres > 0 ? "#7fb2ff"
            : "grey";

        var table = new Table().NoBorder().NoSafeBorder();
        table.AddColumn(new TableColumn("") { Padding = new Padding(1, 0, 1, 0) });
        foreach (var slot in slots)
        {
            table.AddColumn(
                new TableColumn(new Markup($"[grey]{Local(slot.Time):HH:mm}[/]"))
                {
                    Alignment = Justify.Right,
                    Padding = new Padding(0, 0, 1, 0),
                }
            );
        }
        table.AddRow([
            new Markup("[grey]CHANCE[/]"),
            .. slots.Select(x => new Markup($"[{Colour(x)}]{x.Probability}%[/]")),
        ]);
        table.AddRow([
            new Markup("[grey]MM[/]"),
            .. slots.Select(x => new Markup($"[{Colour(x)}]{x.Millimetres:0.0}[/]")),
        ]);

        var wet = slots.FirstOrDefault(x => x.Probability >= 50 || x.Millimetres >= 0.2);
        var summary = wet is null
            ? "[bold]No rain expected[/] [grey]in the next 3 hours[/]"
            : $"[bold #2f7fff]Rain likely from {Local(wet.Time):HH:mm}[/] [grey]({wet.Probability}% chance, {wet.Millimetres:0.0} mm in 15 min)[/]";
        return new Rows(
            radarLine,
            table,
            new SingleLine(
                $" [grey]MODEL[/]  {summary}  [grey]· Open-Meteo, a weather model: it misses most thunderstorms, the radar doesn't[/]"
            )
        );
    }

    /// <summary>
    /// The nearest rain on the radar, how it's moving, and whether it's heading for the circuit.
    /// </summary>
    private string RadarOutlook((double Latitude, double Longitude) at)
    {
        var images = radar.Get(at.Latitude, at.Longitude);
        if (images.Frames.Count == 0)
            return images.Error is null ? "[grey]Loading…[/]" : "[grey]Unavailable[/]";

        var latest = images.Frames[^1];
        var motion = images.Frames.Count > 1 ? images.Frames[^2].ToNext : null;
        var rain = RadarNowcast.Analyse(latest.Pixels, motion);
        var moving = rain.SpeedKmh is { } speed
            ? $" [grey]moving {rain.Heading} at {speed:0} km/h[/]"
            : "";
        var outlook =
            rain.AtCircuit ? $"[bold white on #2f7fff] RAIN AT THE CIRCUIT [/]{moving}"
            : rain.NearestKm is not { } km ? $"[bold]No rain within {RainRadar.RadiusKm:0} km[/]"
            : $"[bold]Rain {km:0} km {rain.Bearing}[/]{moving}"
                + (
                    rain.ArrivesIn is { } arrives
                        ? $" · [bold #2f7fff]reaching the circuit in about {arrives.TotalMinutes:0} min[/]"
                    : rain.SpeedKmh is not null ? " · [grey]not heading for the circuit[/]"
                    : ""
                );
        return $"{outlook}  [grey]· as of {Local(latest.Time):HH:mm}[/]";
    }

    /// <summary>
    /// Which tyres the cars still running are on, the team's cars' tyres, and how the last laps
    /// on wet tyres compare with slicks: the crossover teams watch for when to switch.
    /// </summary>
    private IRenderable Tyres(bool raining)
    {
        var running = timingData
            .Latest.Lines.Where(x => x.Value.Retired != true && x.Value.Stopped != true)
            .Select(x =>
                (
                    Number: x.Key,
                    Line: x.Value,
                    Stint: timingAppData
                        .Latest.Lines.GetValueOrDefault(x.Key)
                        ?.Stints.OrderBy(s => int.Parse(s.Key))
                        .LastOrDefault()
                        .Value
                )
            )
            .Where(x => x.Stint?.Compound is not null)
            .ToList();
        if (running.Count == 0)
            return new Markup("[grey]No tyre data yet.[/]");

        var counts = string.Join(
            "  ",
            new[] { "SOFT", "MEDIUM", "HARD", "INTERMEDIATE", "WET" }.Select(compound =>
                $"{PitWallTheme.CompoundMarkup(compound)} [bold]{running.Count(x => x.Stint!.Compound == compound)}[/]"
            )
        );

        var team = context.Team;
        var teamCars = team is null
            ? ""
            : string.Join(
                "   ",
                team.DriverNumbers.Select(number =>
                {
                    var (_, _, stint) = running.FirstOrDefault(x => x.Number == number);
                    var tla = Markup.Escape(context.Driver(number).Tla ?? number);
                    return stint is null
                        ? $"[bold]{tla}[/] [grey]–[/]"
                        : $"[bold]{tla}[/] {PitWallTheme.CompoundMarkup(stint.Compound)} [grey]{stint.TotalLaps} laps old[/]";
                })
            );

        // Last laps on each kind of tyre, leaving out laps through the pits and out laps
        double? Median(Func<string, bool> compound)
        {
            var laps = running
                .Where(x =>
                    compound(x.Stint!.Compound!)
                    && x.Line.InPit != true
                    && x.Line.PitOut != true
                    && x.Stint.TotalLaps - (x.Stint.StartLaps ?? 0) >= 2
                    && x.Line.LastLapTime.Value.TryParseTimeSpan(out _)
                )
                .Select(x =>
                {
                    x.Line.LastLapTime.Value.TryParseTimeSpan(out var time);
                    return time.TotalSeconds;
                })
                .Order()
                .ToList();
            return laps.Count == 0 ? null : laps[laps.Count / 2];
        }
        static bool IsWet(string compound) => compound is "INTERMEDIATE" or "WET";
        var slicks = Median(x => !IsWet(x));
        var wets = Median(IsWet);

        var crossover = (slicks, wets) switch
        {
            ({ } s, { } w) =>
                $"[grey]Last laps · slicks[/] [bold]{PitWallTheme.LapTime(s)}[/] [grey]· wet tyres[/] [bold]{PitWallTheme.LapTime(w)}[/] → "
                    + (
                        w < s
                            ? $"[bold #2f7fff]wet tyres {s - w:0.0}s faster[/]"
                            : $"[bold]slicks {w - s:0.0}s faster[/]"
                    ),
            (_, null) when raining =>
                "[bold #2f7fff]Raining on track[/] [grey]· nobody on wet tyres yet[/]",
            _ => "[grey]Wet and slick lap times are compared here once cars are on both.[/]",
        };

        return new Rows(
            new Markup($" [grey]ON TRACK[/]  {counts}"),
            new Markup($" [grey]TEAM[/]      {teamCars}"),
            new Markup($" {crossover}")
        );
    }

    private DateTimeOffset Local(DateTimeOffset utc) =>
        utc.ToOffset(
            string.IsNullOrWhiteSpace(sessionInfo.Latest.GmtOffset)
                ? TimeSpan.Zero
                : TimeSpan.Parse(sessionInfo.Latest.GmtOffset)
        );

    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{Math.Max(1, (int)duration.TotalMinutes)} min";

    private static string Compass(int degrees) =>
        new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(degrees / 45.0) % 8];

    /// <summary>An arrow pointing where the wind blows to, with north up.</summary>
    private static string WindArrow(int from) =>
        new[] { "↑", "↗", "→", "↘", "↓", "↙", "←", "↖" }[
            (int)Math.Round(((from + 180) % 360) / 45.0) % 8
        ];
}
