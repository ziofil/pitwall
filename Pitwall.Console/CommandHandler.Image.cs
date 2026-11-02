using System.Diagnostics;
using Pitwall.Console.Graphics;
using SkiaSharp;

namespace Pitwall.Console;

public static partial class CommandHandler
{
    public static async Task OutputImage(FileInfo file, GraphicsProtocol protocol, bool? isVerbose)
    {
        var builder = GetBuilder(isVerbose: isVerbose);

        builder.Services.AddSingleton<TerminalInfoProvider>();

        var app = builder.Build();

        try
        {
            Terminal.EnableRawMode();

            var terminalInfo = app.Services.GetRequiredService<TerminalInfoProvider>();

            var columns = Terminal.Size.Width;
            var rows = Terminal.Size.Height;

            var width = terminalInfo.TerminalSize.Value.Width;
            var height = terminalInfo.TerminalSize.Value.Height;

            var stopwatch = Stopwatch.StartNew();

            var output = protocol switch
            {
                GraphicsProtocol.Sixel => [SixelOutput(file.FullName)],
                GraphicsProtocol.iTerm => [iTermOutput(file.FullName, width / 2, height / 2)],
                GraphicsProtocol.Kitty => KittyOutput(file.FullName, width, height),
                _ => throw new NotImplementedException("Unknown graphics protocol"),
            };

            await Terminal.OutAsync("\r\n");
            foreach (var sequence in output)
            {
                await Terminal.OutAsync(sequence);
            }
            await Terminal.OutAsync("\r\n");

            await Terminal.OutAsync($"Time: {stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            Terminal.DisableRawMode();
        }
    }

    private static string SixelOutput(string filepath)
    {
        var imageFromFile = SKImage.FromEncodedData(filepath);
        var bitmap = SKBitmap.FromImage(imageFromFile);
        return TerminalGraphics.SixelGraphicsSequence(
            Sixel.ImageToSixel(bitmap.Pixels, bitmap.Width)
        );
    }

    private static string iTermOutput(string filepath, int widthPixels, int heightPixels)
    {
        var columns = Terminal.Size.Width;
        var rows = Terminal.Size.Height;

        var imageFromFile = SKImage.FromEncodedData(filepath);
        var outputColumns = imageFromFile.Width / widthPixels * columns;
        var outputRows = imageFromFile.Height / heightPixels * rows;
        var base64Output = Convert.ToBase64String(imageFromFile.Encode().AsSpan());
        return TerminalGraphics.ITerm2GraphicsSequence(outputRows, outputColumns, base64Output);
    }

    private static string[] KittyOutput(string filepath, int widthPixels, int heightPixels)
    {
        var columns = Terminal.Size.Width;
        var rows = Terminal.Size.Height;

        var imageFromFile = SKImage.FromEncodedData(filepath);
        var outputColumns = imageFromFile.Width / widthPixels * columns;
        var outputRows = imageFromFile.Height / heightPixels * rows;
        var base64Output = Convert.ToBase64String(imageFromFile.Encode().AsSpan());
        return TerminalGraphics.KittyGraphicsSequence(outputRows, outputColumns, base64Output);
    }
}
