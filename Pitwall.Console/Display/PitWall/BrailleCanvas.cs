using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// Draws a <see cref="BrailleCanvas"/> sized to the space it is given at render time, so charts
/// fill their layout cell at any terminal size.
/// </summary>
/// <param name="draw">Draws on to a canvas of the available size.</param>
/// <param name="defaultRows">Height used when the container doesn't constrain it.</param>
public sealed class CanvasRenderable(Action<BrailleCanvas> draw, int defaultRows = 10) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var rows = options.Height ?? defaultRows;
        if (rows <= 0 || maxWidth <= 0)
            return [];
        var canvas = new BrailleCanvas(maxWidth, rows);
        draw(canvas);
        return canvas.Render(options, maxWidth);
    }
}

/// <summary>
/// A small plotting surface drawn with Unicode braille characters, giving 2×4 dots per terminal
/// cell. Works in any terminal, unlike inline graphics. Each cell takes the colour of the last
/// dot drawn in it.
/// </summary>
public sealed class BrailleCanvas(int columns, int rows) : IRenderable
{
    private readonly int[,] _dots = new int[columns, rows];
    private readonly Color?[,] _colours = new Color?[columns, rows];
    private readonly Dictionary<(int Column, int Row), (string Text, Style Style)> _labels = [];

    public int Columns => columns;
    public int Rows => rows;

    /// <summary>Width in dots.</summary>
    public int Width => columns * 2;

    /// <summary>Height in dots.</summary>
    public int Height => rows * 4;

    // Braille dot bit for each (x, y) position within a 2×4 cell
    private static readonly int[,] DotBits =
    {
        { 0x01, 0x02, 0x04, 0x40 },
        { 0x08, 0x10, 0x20, 0x80 },
    };

    public void Set(int x, int y, Color colour)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;
        var (column, row) = (x / 2, y / 4);
        _dots[column, row] |= DotBits[x % 2, y % 4];
        _colours[column, row] = colour;
    }

    /// <summary>The colour of the dot at the position, or <c>null</c> if it isn't set.</summary>
    public Color? Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return null;
        var (column, row) = (x / 2, y / 4);
        return (_dots[column, row] & DotBits[x % 2, y % 4]) != 0 ? _colours[column, row] : null;
    }

    public void Line(int x0, int y0, int x1, int y1, Color colour)
    {
        // Bresenham's line algorithm
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            Set(x0, y0, colour);
            if (x0 == x1 && y0 == y1)
                break;
            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>Draws a horizontal dotted guide line across the full width.</summary>
    public void HorizontalGuide(int y, Color colour)
    {
        for (var x = 0; x < Width; x += 4)
            Set(x, y, colour);
    }

    /// <summary>Draws a vertical dotted guide line across the full height.</summary>
    public void VerticalGuide(int x, Color colour)
    {
        for (var y = 0; y < Height; y += 3)
            Set(x, y, colour);
    }

    /// <summary>Writes text into the canvas at a cell position, replacing any dots underneath.</summary>
    public void Label(int column, int row, string text, Style style)
    {
        // An empty label would never advance the render loop
        if (string.IsNullOrEmpty(text) || row < 0 || row >= rows || column < 0)
            return;
        _labels[(Math.Min(column, Math.Max(0, columns - text.Length)), row)] = (text, style);
    }

    /// <summary>
    /// Plots a series of values as a connected line. X values are mapped from
    /// [<paramref name="minX"/>, <paramref name="maxX"/>] and Y values from
    /// [<paramref name="minY"/>, <paramref name="maxY"/>], with larger Y values higher up.
    /// </summary>
    public void Plot(
        IEnumerable<(double X, double Y)> points,
        double minX,
        double maxX,
        double minY,
        double maxY,
        Color colour
    )
    {
        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        if (rangeX <= 0 || rangeY <= 0)
            return;

        (int X, int Y)? previous = null;
        foreach (var (x, y) in points)
        {
            var px = (int)Math.Round((x - minX) / rangeX * (Width - 1));
            var py = (int)
                Math.Round((1 - ((Math.Clamp(y, minY, maxY) - minY) / rangeY)) * (Height - 1));
            if (previous is { } p)
            {
                Line(p.X, p.Y, px, py, colour);
            }
            else
            {
                Set(px, py, colour);
            }
            previous = (px, py);
        }
    }

    public Measurement Measure(RenderOptions options, int maxWidth) =>
        new(Math.Min(columns, maxWidth), Math.Min(columns, maxWidth));

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var width = Math.Min(columns, maxWidth);
        for (var row = 0; row < rows; row++)
        {
            var column = 0;
            while (column < width)
            {
                if (_labels.TryGetValue((column, row), out var label) && label.Text.Length > 0)
                {
                    var text =
                        label.Text.Length > width - column
                            ? label.Text[..(width - column)]
                            : label.Text;
                    yield return new Segment(text, label.Style);
                    column += text.Length;
                    continue;
                }

                // Group runs of cells with the same colour into a single segment
                var colour = _colours[column, row];
                var builder = new StringBuilder();
                while (
                    column < width
                    && _colours[column, row] == colour
                    && !_labels.ContainsKey((column, row))
                )
                {
                    var dots = _dots[column, row];
                    builder.Append(dots == 0 ? ' ' : (char)(0x2800 + dots));
                    column++;
                }
                yield return new Segment(
                    builder.ToString(),
                    colour is null ? Style.Plain : new Style(foreground: colour)
                );
            }
            if (row < rows - 1)
                yield return Segment.LineBreak;
        }
    }
}
