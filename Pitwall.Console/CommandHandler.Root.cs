using Pitwall.Console.Audio;
using Pitwall.Console.Graphics;
using Pitwall.Data;

namespace Pitwall.Console;

public static partial class CommandHandler
{
    public static async Task Root(
        DirectoryInfo? dataDirectory,
        DirectoryInfo? logDirectory,
        bool? isVerbose,
        bool? notifyEnabled,
        bool? preferFfmpeg,
        bool? preventDisplaySleep,
        GraphicsProtocol? forceGraphicsProtocol
    )
    {
        var builder = GetBuilder(
            dataDirectory: dataDirectory,
            logDirectory: logDirectory,
            isVerbose: isVerbose,
            notifyEnabled: notifyEnabled,
            preferFfmpeg: preferFfmpeg,
            preventDisplaySleep: preventDisplaySleep,
            forceGraphicsProtocol: forceGraphicsProtocol
        );

        builder
            .Services.AddSingleton<ConsoleLoop>()
            .AddSingleton<State>()
            .AddInputHandlers()
            .AddDisplays()
            .AddSingleton<INotifyHandler, NotifyHandler>()
            .AddSingleton<TerminalInfoProvider>()
            .AddSingleton<AudioPlayer>()
            .AddHostedService(sp => sp.GetRequiredService<ConsoleLoop>());

        var options = builder.Configuration.Get<ConsoleOptions>() ?? new();

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pitwall");

        logger.LogInformation(
            "{Options}",
            options with
            {
                // Redact the token from logs
                Formula1AccessToken = options.Formula1AccessToken is null
                    ? "<missing>"
                    : "<present>",
            }
        );

        Whisper.net.Logger.LogProvider.AddLogger(
            (level, msg) =>
            {
                switch (level)
                {
                    case Whisper.net.Logger.WhisperLogLevel.Error:
                        logger.LogError("Whisper: {Message}", msg?.Trim('\n'));
                        break;
                    case Whisper.net.Logger.WhisperLogLevel.Warning:
                        logger.LogDebug("Whisper: {Message}", msg?.Trim('\n'));
                        break;
                    case Whisper.net.Logger.WhisperLogLevel.Debug:
                        logger.LogDebug("Whisper: {Message}", msg?.Trim('\n'));
                        break;
                    default:
                        logger.LogDebug("Whisper {Level}: {Message}", level, msg?.Trim('\n'));
                        break;
                }
            }
        );

        await EnsureConfigFileExistsAsync(logger);
        await app.RunAsync();
    }
}
