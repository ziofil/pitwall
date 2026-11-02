namespace Pitwall.Console;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddInputHandlers(this IServiceCollection services)
    {
        services.AddInputHandlersCore();
        return services;
    }

    public static IServiceCollection AddDisplays(this IServiceCollection services)
    {
        services.AddDisplaysCore();

        services.AddSingleton<PitWallContext>();
        services.AddSingleton<MainMenu>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<RadioTranscriber>();
        services.AddSingleton<Graphics.ITerminalImages, Graphics.TerminalImages>();
        services.AddSingleton<PitWallComponents>();
        services.AddSingleton<StartSimulatedSessionOptions>();

        return services;
    }
}
