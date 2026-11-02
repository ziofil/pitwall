using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pitwall.Console;
using Pitwall.Console.Graphics;
using Pitwall.Data;
using SkiaSharp;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.CodeGenerator;

/// <summary>
/// Renders the pit wall pages from a recorded session to PNG files, as they look in a terminal
/// with inline graphics: the terminal text drawn in a monospace font, with the pages' chart and
/// map images on top, and the key hints line at the bottom. Used for the README screenshots.
/// </summary>
public sealed class Screenshots
{
    private const int Columns = 160;
    private const int Rows = 45;
    private const float FontSize = 26;
    private const string ModelFile = "ggml-large-v3-turbo-q5.bin";

    private static readonly SKColor DefaultForeground = SKColor.Parse("dddddd");
    private static readonly SKColor DefaultBackground = SKColor.Parse("0b0b0e");

    /// <summary>Stands in for the terminal: reports image support and keeps the last image.</summary>
    private sealed class CapturingImages(int cellWidth, int cellHeight) : ITerminalImages
    {
        public SKImage? Last { get; private set; }

        public bool Supported => true;

        public (int Columns, int Rows) WindowCells => (Columns, Rows);

        public (int Width, int Height) CellsToPixels(int columns, int rows) =>
            (columns * cellWidth, rows * cellHeight);

        public string[] Encode(SKImage image, int columns, int rows)
        {
            Last?.Dispose();
            using var bitmap = SKBitmap.FromImage(image);
            Last = SKImage.FromBitmap(bitmap.Copy());
            return ["image"];
        }
    }

    public async Task RenderAsync(string repositoryRoot, string session, int lap, string? team)
    {
        using var font = new SKFont(MonospaceTypeface(), FontSize);
        var cellWidth = (int)Math.Ceiling(font.MeasureText("M"));
        var cellHeight = (int)Math.Ceiling(FontSize * 1.3f);
        var images = new CapturingImages(cellWidth, cellHeight);

        // A fresh data directory, with the transcription model from Pitwall's own data directory
        // linked in if it has been downloaded, so the Comms page can show transcribed radio
        var dataDirectory = Path.Join(Path.GetTempPath(), $"pitwall-shots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(dataDirectory, "models"));
        var model = Path.Join(new ConsoleOptions().DataDirectory, "models", ModelFile);
        if (File.Exists(model))
            File.CreateSymbolicLink(Path.Join(dataDirectory, "models", ModelFile), model);
        else
            await Terminal.OutLineAsync(
                $"  No transcription model at {model}: radio won't be transcribed"
            );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataDirectory"] = dataDirectory }
            )
            .Build();
        var services = new ServiceCollection()
            .AddLogging(x => x.AddProvider(NullLoggerProvider.Instance))
            .AddLiveTiming(configuration)
            .Configure<ConsoleOptions>(configuration)
            .AddSingleton<State>()
            .AddDisplays()
            .AddInputHandlers()
            .AddSingleton<AccountLogin>()
            // Needed to create the other screens and key handlers, but never asked anything here
            .AddSingleton<TerminalInfoProvider>()
            .AddSingleton<Pitwall.Console.Audio.AudioPlayer>()
            .AddSingleton<Pitwall.Console.ExternalPlayerSync.WebSocketSynchroniser>()
            .AddSingleton<ITerminalImages>(images)
            .BuildServiceProvider();

        var lapCount = services.GetRequiredService<LapCountProcessor>();
        SessionReplay.Replay(services, session, () => lapCount.Latest.CurrentLap > lap);

        var state = services.GetRequiredService<State>();
        var context = services.GetRequiredService<PitWallContext>();
        state.SelectedTeam = team ?? context.Teams.FirstOrDefault()?.Name;
        await Terminal.OutLineAsync(
            $"  Lap {lapCount.Latest.CurrentLap}, following {state.SelectedTeam}"
        );

        var outputDirectory = Path.Join(repositoryRoot, "docs/images");
        Directory.CreateDirectory(outputDirectory);
        var displays = services.GetServices<IDisplay>().ToList();
        var handlers = services.GetServices<IInputHandler>().ToList();

        // Transcribe the team's radio before drawing the Comms page; it runs on this machine, so
        // it takes a little while per message
        var transcriber = services.GetRequiredService<RadioTranscriber>();
        if (transcriber.ModelAvailable)
        {
            await Terminal.OutLineAsync("  Transcribing the team's radio...");
            var deadline = DateTime.UtcNow.AddMinutes(10);
            transcriber.Tick();
            while (!transcriber.Finished && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                transcriber.Tick();
            }
            if (transcriber.Problem is { } problem)
                await Terminal.OutLineAsync($"  Transcription problem: {problem}");
        }

        foreach (var (screen, name) in PitWallComponents.Pages)
        {
            state.CurrentScreen = screen;
            state.CursorOffset = 0;
            var display = displays.Single(x => x.Screen == screen);
            var content = await display.GetContentAsync();

            var page = new Layout("Root").SplitRows(
                new Layout("Content", content),
                new Layout("Footer", Footer(handlers, state)).Size(1)
            );
            // Pages that draw an image keep it in a private field; find where it goes
            var inline =
                display
                    .GetType()
                    .GetField("_image", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(display) as InlineImage;
            var area = inline?.Area;

            using var screenshot = Draw(
                page,
                font,
                cellWidth,
                cellHeight,
                area is null ? null : (area.Value, images.Last!)
            );
            using var data = screenshot.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.Join(outputDirectory, $"{name.ToLowerInvariant()}.png");
            await File.WriteAllBytesAsync(path, data.ToArray());
            await Terminal.OutLineAsync($"  Wrote {Path.GetRelativePath(repositoryRoot, path)}");
        }
    }

    /// <summary>The key hints line, as the app draws it under every page.</summary>
    private static IRenderable Footer(List<IInputHandler> handlers, State state) =>
        new Columns(
            handlers
                .Where(x => x.IsEnabled && x.ApplicableScreens.Contains(state.CurrentScreen))
                .OrderBy(x => x.Sort)
                .Select(x => new Markup(
                    $"[[{x.DisplayKeys.ToDisplayCharacters()}]] {x.Description}"
                ))
        ).Collapse();

    private static SKImage Draw(
        IRenderable page,
        SKFont font,
        int cellWidth,
        int cellHeight,
        ((int Row, int Column, int Columns, int Rows) Area, SKImage Image)? overlay
    )
    {
        var console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor,
                Out = new AnsiConsoleOutput(new StringWriter()),
                Interactive = InteractionSupport.No,
            }
        );
        console.Profile.Width = Columns;
        console.Profile.Height = Rows;
        var segments = page.Render(
            RenderOptions.Create(console, console.Profile.Capabilities),
            Columns
        );

        using var surface = SKSurface.Create(
            new SKImageInfo(Columns * cellWidth, Rows * cellHeight)
        );
        var canvas = surface.Canvas;
        canvas.Clear(DefaultBackground);

        var fallbacks = new Dictionary<int, SKFont>();
        var (column, row) = (0, 0);
        foreach (var segment in segments)
        {
            if (segment.IsLineBreak)
            {
                (column, row) = (0, row + 1);
                continue;
            }
            if (segment.IsControlCode)
                continue;

            var style = segment.Style;
            var foreground = ToSk(style.Foreground, DefaultForeground);
            var background = ToSk(style.Background, DefaultBackground);
            if (style.Decoration.HasFlag(Decoration.Invert))
                (foreground, background) = (background, foreground);
            if (style.Decoration.HasFlag(Decoration.Dim))
                foreground = foreground.WithAlpha(150);

            using var fill = new SKPaint { Color = background };
            using var ink = new SKPaint { Color = foreground, IsAntialias = true };
            foreach (var rune in segment.Text.EnumerateRunes())
            {
                var x = column * cellWidth;
                var y = row * cellHeight;
                if (background != DefaultBackground)
                    canvas.DrawRect(x, y, cellWidth, cellHeight, fill);
                if (!Rune.IsWhiteSpace(rune))
                {
                    var glyphFont = font.ContainsGlyph(rune.Value)
                        ? font
                        : Fallback(fallbacks, font, rune);
                    glyphFont.Embolden = style.Decoration.HasFlag(Decoration.Bold);
                    canvas.DrawText(rune.ToString(), x, y + (cellHeight * 0.75f), glyphFont, ink);
                    glyphFont.Embolden = false;
                }
                column++;
            }
        }

        if (overlay is { } image)
        {
            var (area, picture) = image;
            var destination = SKRect.Create(
                area.Column * cellWidth,
                area.Row * cellHeight,
                area.Columns * cellWidth,
                area.Rows * cellHeight
            );
            canvas.DrawImage(
                picture,
                destination,
                new SKSamplingOptions(SKCubicResampler.Mitchell)
            );
        }

        return surface.Snapshot();
    }

    private static SKFont Fallback(Dictionary<int, SKFont> fallbacks, SKFont font, Rune rune)
    {
        if (fallbacks.TryGetValue(rune.Value, out var cached))
            return cached;
        var typeface = SKFontManager.Default.MatchCharacter(rune.Value) ?? font.Typeface;
        var fallback = new SKFont(typeface, font.Size);
        fallbacks[rune.Value] = fallback;
        return fallback;
    }

    private static SKTypeface MonospaceTypeface() =>
        new[] { "Menlo", "SF Mono", "DejaVu Sans Mono", "Consolas", "Courier New" }
            .Select(name => SKFontManager.Default.MatchFamily(name))
            .FirstOrDefault(x => x is not null)
        ?? SKTypeface.Default;

    private static SKColor ToSk(Color colour, SKColor fallback) =>
        colour == Color.Default ? fallback : new SKColor(colour.R, colour.G, colour.B);
}
