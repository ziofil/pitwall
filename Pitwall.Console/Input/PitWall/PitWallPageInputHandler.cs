namespace Pitwall.Console;

/// <summary>Moves between the pit wall pages with the arrow keys, or jumps to one with 1–6.</summary>
public sealed class PitWallPageInputHandler(State state) : IInputHandler
{
    private static readonly ConsoleKey[] NumberKeys =
    [
        ConsoleKey.D1,
        ConsoleKey.D2,
        ConsoleKey.D3,
        ConsoleKey.D4,
        ConsoleKey.D5,
        ConsoleKey.D6,
    ];

    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => PitWallComponents.Pages.Select(x => x.Screen).ToArray();

    public ConsoleKey[] Keys =>
        [ConsoleKey.LeftArrow, ConsoleKey.H, ConsoleKey.RightArrow, ConsoleKey.L, .. NumberKeys];

    public ConsoleKey[] DisplayKeys => [ConsoleKey.LeftArrow, ConsoleKey.RightArrow];

    public string Description => "Page";

    public int Sort => 20;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        var pages = PitWallComponents.Pages;
        var index = Array.FindIndex(pages, x => x.Screen == state.CurrentScreen);
        var numberIndex = Array.IndexOf(NumberKeys, consoleKeyInfo.Key);

        var newIndex =
            numberIndex >= 0 ? numberIndex
            : consoleKeyInfo.Key is ConsoleKey.LeftArrow or ConsoleKey.H ? index - 1
            : index + 1;
        newIndex = ((newIndex % pages.Length) + pages.Length) % pages.Length;

        if (pages[newIndex].Screen == state.CurrentScreen)
            return;

        await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full), cancellationToken);
        state.CurrentScreen = pages[newIndex].Screen;
        state.CursorOffset = 0;
    }
}
