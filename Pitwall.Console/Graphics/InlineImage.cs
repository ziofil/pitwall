using System.Diagnostics;
using SkiaSharp;

namespace Pitwall.Console.Graphics;

/// <summary>
/// One image drawn over an area of the screen, for pages that draw charts or maps as real
/// images in terminals that support inline graphics. Keeps the cost per frame down: images are
/// rendered at a capped resolution and scaled up by the terminal, compressed with fast PNG
/// settings, only re-sent when they change, and the page's frame rate drops when frames get
/// expensive (e.g. when the CPU is busy) instead of falling behind.
/// </summary>
public sealed class InlineImage(ITerminalImages images)
{
    /// <summary>Images are rendered no wider or taller than this, in pixels.</summary>
    public const int MaxPixels = 1600;

    private static readonly TimeSpan FastestFrame = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan SlowestFrame = TimeSpan.FromMilliseconds(500);

    private string[] _sequence = [];
    private string[] _previous = [];
    private (int Row, int Column) _position;
    private double _averageCostMs;

    public bool Supported => images.Supported;

    /// <summary>
    /// How often the page should redraw: as fast as 15 fps, slower if rendering and sending the
    /// image takes a large share of each frame.
    /// </summary>
    public TimeSpan FrameInterval
    {
        get
        {
            var interval = TimeSpan.FromMilliseconds(_averageCostMs * 3);
            return interval < FastestFrame ? FastestFrame
                : interval > SlowestFrame ? SlowestFrame
                : interval;
        }
    }

    /// <summary>The terminal window size in cells.</summary>
    public (int Columns, int Rows) WindowCells => images.WindowCells;

    /// <summary>
    /// The pixel size to render an area of cells at: the terminal's own pixel size, scaled down
    /// to at most <see cref="MaxPixels"/> on the longest side.
    /// </summary>
    public (int Width, int Height) PixelSize(int columns, int rows)
    {
        var (width, height) = images.CellsToPixels(columns, rows);
        var scale = Math.Min(1.0, MaxPixels / (double)Math.Max(1, Math.Max(width, height)));
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    /// <summary>
    /// Renders and encodes the image for the given area. Call from GetContentAsync, then draw it
    /// with <see cref="DrawAsync"/> from PostContentDrawAsync.
    /// </summary>
    /// <param name="render">Draws the image at the given pixel size.</param>
    public void Update(int row, int column, int columns, int rows, Func<int, int, SKImage> render)
    {
        if (columns <= 0 || rows <= 0)
        {
            Clear();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var (width, height) = PixelSize(columns, rows);
        using var image = render(width, height);
        _sequence = images.Encode(image, columns, rows);
        _position = (row, column);

        // Smoothed cost of a frame, which sets the frame rate
        var cost = stopwatch.Elapsed.TotalMilliseconds;
        _averageCostMs = _averageCostMs == 0 ? cost : (_averageCostMs * 0.8) + (cost * 0.2);
    }

    /// <summary>Stops drawing the image, e.g. when the page shows a message instead.</summary>
    public void Clear()
    {
        _sequence = [];
        _previous = [];
    }

    /// <summary>
    /// Draws the image over the screen if the text underneath was just redrawn or the image
    /// changed since it was last sent.
    /// </summary>
    public async Task DrawAsync(bool textRedrawn)
    {
        if (_sequence.Length == 0)
            return;
        if (!textRedrawn && _previous.SequenceEqual(_sequence))
            return;

        await Terminal.OutAsync(ControlSequences.MoveCursorTo(_position.Row, _position.Column));
        foreach (var part in _sequence)
            await Terminal.OutAsync(part);
        _previous = _sequence;
    }
}
