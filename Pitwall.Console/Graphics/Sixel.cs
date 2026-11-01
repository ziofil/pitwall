using System.Text;
using SkiaSharp;

namespace Pitwall.Console.Graphics;

public static class Sixel
{
    private const char CarriageReturn = '$';
    private const char LineFeed = '-';
    private const byte ColourSpace = 4;

    /// <summary>
    /// Converts the given list of pixels (representing an image) in to a set of encoded sixels.
    /// Only the sixel data is returned, without the escape code header and trailer.
    /// </summary>
    public static string ImageToSixel(SKColor[] pixels, int width)
    {
        var reducedColourSpacePixels = pixels.Select(ReducedColorSpace);

        var colourToColourId = reducedColourSpacePixels
            .Distinct()
            .Where(x => x.Alpha > 0)
            .Select((colour, i) => (colour, i))
            .ToDictionary(x => x.colour, x => x.i);

        var sixels = reducedColourSpacePixels
            .Chunk(width) // Make a 2d array of pixels
            .Chunk(6) // Process chunks of 6 rows at a time, full width
            .Select(rowChunk => SixelChars(colourToColourId, rowChunk, width));

        var colourRegisters = GetColourRegister(colourToColourId);
        var sixelData = string.Concat(sixels);

        return colourRegisters + sixelData;
    }

    private static string SixelChars(
        Dictionary<SKColor, int> colourToColourId,
        SKColor[][] rows,
        int width
    )
    {
        // rows will be a 6 * width array of colours
        // process each row and create sixel characters
        // each row needs to be processed for each colour
        var sixels = new StringBuilder();
        foreach (var (colour, colourId) in colourToColourId)
        {
            var rowOfSixelsForColour = SixelCharsForColour(colour, colourId, rows, width);
            if (rowOfSixelsForColour is not null)
            {
                // Only append the sixels if they actually draw any colour
                sixels.Append(rowOfSixelsForColour);
            }
        }

        // Add a line feed as all colours have been output for the current chunk of rows
        sixels.Append(LineFeed);

        return sixels.ToString();
    }

    private static string? SixelCharsForColour(
        SKColor colour,
        int colourId,
        SKColor[][] rows,
        int width
    )
    {
        var sixels = new StringBuilder();

        var prevSixel = (char)0;
        var count = 0;
        for (var column = 0; column < width; column++)
        {
            var newSixel = (char)0;
            for (var row = 0; row < rows.Length; row++)
            {
                if (rows[row][column] == colour)
                {
                    // Use the row number to set the correct bit of the sixel for the current column
                    newSixel |= (char)(1 << row);
                }
            }

            if (prevSixel != newSixel)
            {
                // Not a repeat sixel, so write the previous sixel
                sixels.AppendSixel(prevSixel, count);
                prevSixel = newSixel;
                count = 1;
            }
            else
            {
                count++;
            }
        }
        sixels.AppendSixel(prevSixel, count);

        // Output a line for the current colour, ending with a carriage return so the cursor is ready to draw the next colour
        // Return null if the entire line is just 0's
        // If we've looped through all the pixels and not found anything, then count will equal width
        return count != width ? $"#{colourId}{sixels}{CarriageReturn}" : null;
    }

    private static void AppendSixel(this StringBuilder sixels, char sixel, int count) =>
        sixels.Append(
            count switch
            {
                0 => string.Empty,
                1 => (char)(sixel + 63),
                _ => $"!{count}{(char)(sixel + 63)}",
            }
        );

    private static string GetColourRegister(Dictionary<SKColor, int> colours)
    {
        var register = new StringBuilder();
        return string.Concat(
            colours.Select(x =>
                $"#{x.Value};2;{x.Key.Red * 100 / ColourSpace};{x.Key.Green * 100 / ColourSpace};{x.Key.Blue * 100 / ColourSpace}"
            )
        );
    }

    private static SKColor ReducedColorSpace(SKColor colour) =>
        new(
            (byte)(colour.Red * ColourSpace / 255),
            (byte)(colour.Green * ColourSpace / 255),
            (byte)(colour.Blue * ColourSpace / 255),
            (byte)(colour.Alpha >= 50 ? 1 : 0)
        );
}
