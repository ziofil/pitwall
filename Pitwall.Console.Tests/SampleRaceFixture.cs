using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console.Tests;

/// <summary>
/// Replays the recorded 2024 Silverstone race from the repository's sample data up to lap 30.
/// </summary>
public sealed class SampleRaceFixture() : SampleSessionFixture("2024_Silverstone_Race", ReplayToLap)
{
    public const int ReplayToLap = 30;
}

/// <summary>
/// Replays the 2026 Mexico City Grand Prix to lap 56: a recording joined mid-race (at lap 29),
/// with a safety car and a red flag.
/// </summary>
public sealed class SampleMexicoFixture() : SampleSessionFixture("2026_Mexico_City_Race", 55);

/// <summary>Replays the whole recorded 2024 Imola qualifying session.</summary>
public sealed class SampleQualifyingFixture()
    : SampleSessionFixture("2024_Imola_Qualifying", int.MaxValue);

/// <summary>
/// Replays a recorded session from the repository's sample data up to a set lap, so displays can
/// be rendered against real timing data.
/// </summary>
public abstract class SampleSessionFixture
{
    public IServiceProvider Services { get; }

    /// <summary>Whether the circuit outline could be downloaded for the track page.</summary>
    public bool HasCircuitMap { get; }

    protected SampleSessionFixture(string session, int replayToLap)
    {
        // An empty data directory, so tests never find a downloaded Whisper model and start
        // transcribing (which would download radio audio)
        var dataDirectory = Path.Join(Path.GetTempPath(), $"pitwall-tests-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataDirectory"] = dataDirectory }
            )
            .Build();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(configuration)
            .AddSingleton<State>()
            .AddSingleton<PitWallContext>()
            .AddSingleton<PitWallComponents>()
            .Configure<ConsoleOptions>(_ => { })
            .AddSingleton<NoTerminalImages>()
            .AddSingleton<Graphics.ITerminalImages>(x => x.GetRequiredService<NoTerminalImages>())
            .AddSingleton<StartSimulatedSessionOptions>()
            .AddSingleton<MainMenu>()
            .AddSingleton<SettingsStore>()
            .AddSingleton<SettingsDisplay>()
            .AddSingleton<RadioTranscriber>()
            .AddSingleton<MainDisplay>()
            .AddSingleton<SelectTeamDisplay>()
            .AddSingleton<PitWallTimingDisplay>()
            .AddSingleton<PitWallStrategyDisplay>()
            .AddSingleton<PitWallTelemetryDisplay>()
            .AddSingleton<PitWallTrackMapDisplay>()
            .AddSingleton<PitWallCommsDisplay>();
        Services = services.BuildServiceProvider();

        var lapCount = Services.GetRequiredService<LapCountProcessor>();
        SessionReplay.Replay(
            Services,
            FindSampleDirectory(session),
            () => lapCount.Latest.CurrentLap > replayToLap
        );

        // Circuits are shipped with Pitwall, so the map is always there for known circuits
        HasCircuitMap =
            Services.GetRequiredService<SessionInfoProcessor>().Latest.CircuitPoints.Count > 0;
    }

    /// <summary>
    /// Renders a display to plain text at the given terminal size. Also writes the text and a
    /// coloured HTML version to a <c>renders</c> folder next to the test assembly (or to
    /// <c>PITWALL_RENDER_DIR</c> if set), for looking at the result.
    /// </summary>
    public async Task<string> RenderAsync(IDisplay display, int width = 160, int height = 45)
    {
        var content = await display.GetContentAsync();
        var console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor,
                Out = new AnsiConsoleOutput(new StringWriter()),
                Interactive = InteractionSupport.No,
            }
        );
        console.Profile.Width = width;
        console.Profile.Height = height;
        var recorder = new Recorder(console);
        recorder.Write(content);
        var text = recorder.ExportText();

        var outputDirectory = Environment.GetEnvironmentVariable("PITWALL_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            outputDirectory = Path.Join(AppContext.BaseDirectory, "renders");

        Directory.CreateDirectory(outputDirectory);
        var name = $"{GetType().Name}-{display.Screen}-{width}x{height}";
        await File.WriteAllTextAsync(Path.Join(outputDirectory, $"{name}.txt"), text);
        var html = recorder
            .ExportHtml()
            .Replace(
                "<pre",
                "<pre style=\"background:#000;color:#ddd;font-family:Menlo,monospace;font-size:13px;line-height:1.2;margin:0;padding:8px\""
            );
        await File.WriteAllTextAsync(
            Path.Join(outputDirectory, $"{name}.html"),
            $"<html><body style='margin:0;background:#000'>{html}</body></html>"
        );

        return text;
    }

    private static string FindSampleDirectory(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(directory.FullName, "Sample Data", name);
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Couldn't find Sample Data/{name}");
    }
}

/// <summary>
/// A stand-in for the terminal's image support. By default it reports no inline graphics, so
/// pages use their text fallbacks; tests can switch it on and look at the last image encoded.
/// </summary>
internal sealed class NoTerminalImages : Graphics.ITerminalImages
{
    public bool Supported { get; set; }

    /// <summary>The last image a page sent, as PNG.</summary>
    public byte[]? LastImage { get; private set; }

    public (int Columns, int Rows) WindowCells => (160, 45);

    // A high-resolution screen: 18x36 pixels per cell
    public (int Width, int Height) CellsToPixels(int columns, int rows) =>
        (columns * 18, rows * 36);

    public string[] Encode(SkiaSharp.SKImage image, int columns, int rows)
    {
        using var data = Graphics.SKImageExtensions.EncodeFastPng(image);
        LastImage = data.ToArray();
        return ["image"];
    }
}
