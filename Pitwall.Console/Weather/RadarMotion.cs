using SkiaSharp;

namespace Pitwall.Console;

/// <summary>A radar frame's pixels, premultiplied RGBA, for working out motion between frames.</summary>
public sealed record RadarPixels(int Width, int Height, byte[] Rgba)
{
    public static RadarPixels From(SKImage image)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
        );
        image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return new RadarPixels(image.Width, image.Height, bitmap.GetPixelSpan().ToArray());
    }
}

/// <summary>
/// How the rain moved between two radar frames: a displacement in pixels for each block of the
/// image, smoothed so neighbouring blocks move together.
/// </summary>
public sealed class RadarFlow(int columns, int rows, int blockSize, float[] dx, float[] dy)
{
    public int BlockSize => blockSize;

    /// <summary>The displacement at a pixel, interpolated between the block centres.</summary>
    public (float X, float Y) At(float x, float y)
    {
        var gx = Math.Clamp((x / blockSize) - 0.5f, 0, columns - 1);
        var gy = Math.Clamp((y / blockSize) - 0.5f, 0, rows - 1);
        var (x0, y0) = ((int)gx, (int)gy);
        var (x1, y1) = (Math.Min(x0 + 1, columns - 1), Math.Min(y0 + 1, rows - 1));
        var (fx, fy) = (gx - x0, gy - y0);
        float Lerp(float[] field) =>
            (field[(y0 * columns) + x0] * (1 - fx) * (1 - fy))
            + (field[(y0 * columns) + x1] * fx * (1 - fy))
            + (field[(y1 * columns) + x0] * (1 - fx) * fy)
            + (field[(y1 * columns) + x1] * fx * fy);
        return (Lerp(dx), Lerp(dy));
    }
}

/// <summary>
/// Makes the radar loop move smoothly between its 10-minute frames: estimates how the rain
/// moved from one frame to the next, then draws the frames in between by moving both frames
/// part of the way along that motion and blending them.
/// </summary>
public static class RadarMotion
{
    private const int BlockSize = 12;

    /// <summary>Furthest rain is searched for between frames, in pixels (about 15 km at zoom 7).</summary>
    private const int SearchRadius = 14;

    /// <summary>
    /// Each block is matched by a window this much wider on every side, so that inside a large
    /// even patch of rain the window still takes in some edges to match on.
    /// </summary>
    private const int Margin = 8;

    /// <summary>
    /// Windows varying less than this (as a standard deviation of intensity) have nothing to
    /// match on, so their block takes the motion of the rest.
    /// </summary>
    private const double MinTexture = 6;

    /// <summary>Estimates the motion from one frame to the next by matching blocks of rain.</summary>
    public static RadarFlow Estimate(RadarPixels from, RadarPixels to)
    {
        var a = Intensity(from);
        var b = Intensity(to);
        var (width, height) = (from.Width, from.Height);
        var columns = Math.Max(1, width / BlockSize);
        var rows = Math.Max(1, height / BlockSize);
        var dx = new float[columns * rows];
        var dy = new float[columns * rows];
        var known = new bool[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = Math.Max(0, (column * BlockSize) - Margin);
                var top = Math.Max(0, (row * BlockSize) - Margin);
                var right = Math.Min(width, ((column + 1) * BlockSize) + Margin);
                var bottom = Math.Min(height, ((row + 1) * BlockSize) + Margin);

                var (sum, sumSquares, count) = (0.0, 0.0, 0);
                for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                {
                    var value = a[(y * width) + x];
                    (sum, sumSquares, count) = (
                        sum + value,
                        sumSquares + (value * value),
                        count + 1
                    );
                }
                var mean = sum / count;
                if (Math.Sqrt(Math.Max(0, (sumSquares / count) - (mean * mean))) < MinTexture)
                    continue;

                // The offset where the window best matches the next frame, preferring small moves
                // when several match equally well
                var best = double.MaxValue;
                var (bestX, bestY) = (0, 0);
                for (var oy = -SearchRadius; oy <= SearchRadius; oy++)
                {
                    for (var ox = -SearchRadius; ox <= SearchRadius; ox++)
                    {
                        var cost = ((ox * ox) + (oy * oy)) * 0.5;
                        for (var y = top; y < bottom && cost < best; y++)
                        {
                            var ty = Math.Clamp(y + oy, 0, height - 1) * width;
                            for (var x = left; x < right; x++)
                                cost += Math.Abs(
                                    a[(y * width) + x] - b[ty + Math.Clamp(x + ox, 0, width - 1)]
                                );
                        }
                        if (cost < best)
                            (best, bestX, bestY) = (cost, ox, oy);
                    }
                }
                var index = (row * columns) + column;
                (dx[index], dy[index], known[index]) = (bestX, bestY, true);
            }
        }

        // Blocks with nothing to match take the average motion of those with it, then everything is
        // smoothed so the rain moves as a whole rather than block by block
        var knownCount = known.Count(x => x);
        var (meanX, meanY) =
            knownCount == 0
                ? (0f, 0f)
                : (
                    dx.Where((_, i) => known[i]).Sum() / knownCount,
                    dy.Where((_, i) => known[i]).Sum() / knownCount
                );
        for (var i = 0; i < known.Length; i++)
        {
            if (!known[i])
                (dx[i], dy[i]) = (meanX, meanY);
        }
        for (var pass = 0; pass < 3; pass++)
        {
            dx = Smooth(dx, columns, rows);
            dy = Smooth(dy, columns, rows);
        }
        return new RadarFlow(columns, rows, BlockSize, dx, dy);
    }

    /// <summary>
    /// The frame a fraction <paramref name="t"/> of the way from one frame to the next: each
    /// pixel is taken from the first frame back along the motion and from the second frame
    /// forward along it, blended by how close it is to each.
    /// </summary>
    public static SKImage Between(RadarPixels from, RadarPixels to, RadarFlow flow, float t)
    {
        var (width, height) = (from.Width, from.Height);
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (vx, vy) = flow.At(x + 0.5f, y + 0.5f);
                var o = ((y * width) + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var value =
                        ((1 - t) * Sample(from, x - (t * vx), y - (t * vy), c))
                        + (t * Sample(to, x + ((1 - t) * vx), y + ((1 - t) * vy), c));
                    output[o + c] = (byte)Math.Clamp(value + 0.5f, 0, 255);
                }
            }
        }
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, output);
    }

    private static float[] Intensity(RadarPixels frame)
    {
        var values = new float[frame.Width * frame.Height];
        for (var i = 0; i < values.Length; i++)
            values[i] =
                (
                    frame.Rgba[i * 4]
                    + frame.Rgba[(i * 4) + 1]
                    + frame.Rgba[(i * 4) + 2]
                    + frame.Rgba[(i * 4) + 3]
                ) / 4f;
        return values;
    }

    /// <summary>A channel of the frame at a fractional pixel position, interpolated bilinearly.</summary>
    private static float Sample(RadarPixels frame, float x, float y, int channel)
    {
        x = Math.Clamp(x - 0.5f, 0, frame.Width - 1);
        y = Math.Clamp(y - 0.5f, 0, frame.Height - 1);
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(x0 + 1, frame.Width - 1), Math.Min(y0 + 1, frame.Height - 1));
        var (fx, fy) = (x - x0, y - y0);
        float At(int px, int py) => frame.Rgba[(((py * frame.Width) + px) * 4) + channel];
        return (At(x0, y0) * (1 - fx) * (1 - fy))
            + (At(x1, y0) * fx * (1 - fy))
            + (At(x0, y1) * (1 - fx) * fy)
            + (At(x1, y1) * fx * fy);
    }

    private static float[] Smooth(float[] field, int columns, int rows)
    {
        var result = new float[field.Length];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var (sum, count) = (0f, 0);
                for (var r = Math.Max(0, row - 1); r <= Math.Min(rows - 1, row + 1); r++)
                for (var c = Math.Max(0, column - 1); c <= Math.Min(columns - 1, column + 1); c++)
                    (sum, count) = (sum + field[(r * columns) + c], count + 1);
                result[(row * columns) + column] = sum / count;
            }
        }
        return result;
    }
}
