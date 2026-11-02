namespace Pitwall.Console;

/// <summary>
/// Enter on the settings screen changes the setting under the cursor: on/off settings switch,
/// choices move to the next option, and folders and addresses open a text box.
/// </summary>
public sealed class SettingsInputHandler(State state, SettingsStore store) : IInputHandler
{
    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.Settings];

    public ConsoleKey[] Keys => [ConsoleKey.Enter];

    public string Description =>
        store.Settings.ElementAtOrDefault(state.CursorOffset)?.Kind
            is SettingKind.Folder
                or SettingKind.Text
            ? "Edit"
            : "Change";

    public int Sort => 40;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        var setting = store.Settings.ElementAtOrDefault(state.CursorOffset);
        if (setting is null)
            return;

        switch (setting.Kind)
        {
            case SettingKind.Toggle or SettingKind.Choice:
                await store.SaveAsync(setting, store.Next(setting));
                break;
            default:
                state.TextInput = new TextInput(
                    store.Pending(setting) ?? store.Value(setting),
                    text => store.SaveAsync(setting, text)
                );
                break;
        }
    }
}
