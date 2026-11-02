namespace Pitwall.Console;

/// <summary>Opens the launch screen menu entry under the cursor.</summary>
public sealed class MainMenuInputHandler(MainMenu menu) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.Main];

    public ConsoleKey[] Keys => [ConsoleKey.Enter, ConsoleKey.RightArrow];

    public ConsoleKey[] DisplayKeys => [ConsoleKey.Enter];

    public string Description => "Open";

    public int Sort => 40;

    public Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    ) => menu.Items[menu.SelectedIndex].Activate();
}
