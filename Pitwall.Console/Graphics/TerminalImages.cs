using SkiaSharp;

namespace Pitwall.Console.Graphics;

/// <summary>Draws images inline in the terminal, when the terminal supports it.</summary>
public interface ITerminalImages
{
    /// <summary>Whether the terminal supports an inline graphics protocol (Kitty, iTerm2 or Sixel).</summary>
    bool Supported { get; }

    /// <summary>The size of the terminal window in cells.</summary>
    (int Columns, int Rows) WindowCells { get; }

    /// <summary>Converts a number of terminal cells to pixels, as (width, height).</summary>
    (int Width, int Height) CellsToPixels(int columns, int rows);

    /// <summary>The escape sequences that draw the image over the given number of cells.</summary>
    string[] Encode(SKImage image, int columns, int rows);
}

/// <summary>Inline images through the protocols the terminal was detected to support.</summary>
public sealed class TerminalImages(TerminalInfoProvider terminalInfo) : ITerminalImages
{
    public bool Supported =>
        terminalInfo.IsITerm2ProtocolSupported.Value
        || terminalInfo.IsKittyProtocolSupported.Value
        || terminalInfo.IsSixelSupported.Value;

    public (int Columns, int Rows) WindowCells =>
        (terminalInfo.TerminalSize.Value.Columns, terminalInfo.TerminalSize.Value.Rows);

    public (int Width, int Height) CellsToPixels(int columns, int rows)
    {
        var size = terminalInfo.TerminalSize.Value;
        return (size.ColumnsToPixels(columns), size.RowsToPixels(rows));
    }

    public string[] Encode(SKImage image, int columns, int rows) =>
        image.ToGraphicsSequence(terminalInfo, rows, columns);
}
