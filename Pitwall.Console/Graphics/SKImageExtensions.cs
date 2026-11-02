using SkiaSharp;

namespace Pitwall.Console.Graphics;

internal static class SKImageExtensions
{
    public static string[] ToGraphicsSequence(
        this SKImage image,
        TerminalInfoProvider terminalInfo,
        int windowHeight,
        int windowWidth
    )
    {
        if (terminalInfo.IsITerm2ProtocolSupported.Value)
        {
            var imageData = image.EncodeFastPng();
            var base64 = Convert.ToBase64String(imageData.AsSpan());
            return [TerminalGraphics.ITerm2GraphicsSequence(windowHeight, windowWidth, base64)];
        }
        else if (terminalInfo.IsKittyProtocolSupported.Value)
        {
            var imageData = image.EncodeFastPng();
            var base64 = Convert.ToBase64String(imageData.AsSpan());
            return
            [
                TerminalGraphics.KittyGraphicsSequenceDelete(),
                .. TerminalGraphics.KittyGraphicsSequence(windowHeight, windowWidth, base64),
            ];
        }
        else if (terminalInfo.IsSixelSupported.Value)
        {
            var bitmap = SKBitmap.FromImage(image);
            var sixelData = Sixel.ImageToSixel(bitmap.Pixels, bitmap.Width);
            return [TerminalGraphics.SixelGraphicsSequence(sixelData)];
        }

        return
        [
            "Unexpected error whilst creating graphics, we shouldn't have got here. Please report!",
        ];
    }

    /// <summary>
    /// Encodes as PNG with the fastest compression. Images are re-sent many times a second, so
    /// encoding speed matters far more than a slightly smaller file.
    /// </summary>
    public static SKData EncodeFastPng(this SKImage image)
    {
        using var pixmap = image.PeekPixels();
        return pixmap is null
            ? image.Encode()
            : pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 1));
    }
}
