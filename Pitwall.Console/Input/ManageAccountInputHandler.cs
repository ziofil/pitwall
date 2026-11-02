using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>
/// On the account screen: when logged out, Enter opens the login window. When logged in, the
/// arrows pick one of <see cref="LoggedInOptions"/> and Enter runs it.
/// </summary>
public sealed class ManageAccountInputHandler(
    State state,
    AccountLogin accountLogin,
    Formula1Account accountService
) : IInputHandler
{
    /// <summary>What can be done when already logged in, in the order shown.</summary>
    public static readonly string[] LoggedInOptions = ["Log in again", "Log out", "Back to menu"];

    public bool IsEnabled => true;

    public Screen[] ApplicableScreens => [Screen.ManageAccount];

    public ConsoleKey[] Keys => [ConsoleKey.Enter];

    private bool LoggedIn =>
        accountService.IsAuthenticated == Formula1Account.AuthenticationResult.Success;

    public string Description =>
        !_loginTask.IsCompleted
            ? _status switch
            {
                StatusFlags.TokenReceived =>
                    "[olive]Token received, please close browser window[/]",
                _ => "[olive]Waiting for browser window to be closed[/]",
            }
        : LoggedIn ? "Select"
        : accountLogin.WindowUsed && _status != StatusFlags.Complete
            ? "[yellow]Restart Pitwall to log in again[/]"
        : _status switch
        {
            StatusFlags.Failed => "[red]Login failed, restart Pitwall to try again[/]",
            _ => "Login",
        };

    public int Sort => 40;

    private StatusFlags _status = StatusFlags.None;

    private Task _loginTask = Task.CompletedTask;

    public async Task ExecuteAsync(
        ConsoleKeyInfo consoleKeyInfo,
        CancellationToken cancellationToken = default
    )
    {
        if (!_loginTask.IsCompleted)
            return;

        if (LoggedIn)
        {
            switch (LoggedInOptions.ElementAtOrDefault(state.CursorOffset))
            {
                case "Log in again":
                    await StartLoginAsync();
                    break;
                case "Log out":
                    await accountLogin.LogoutAsync();
                    state.CursorOffset = 0;
                    break;
                default:
                    await Terminal.OutAsync(
                        ControlSequences.ClearScreen(ClearMode.Full),
                        cancellationToken
                    );
                    state.CurrentScreen = Screen.Main;
                    state.CursorOffset = 0;
                    break;
            }
            return;
        }

        await StartLoginAsync();
    }

    private async Task StartLoginAsync()
    {
        if (accountLogin.WindowUsed)
            return;

        _status = StatusFlags.Waiting;
        _loginTask = accountLogin.LoginAsync(status =>
            _status = status switch
            {
                AccountLogin.LoginStatus.TokenReceived => StatusFlags.TokenReceived,
                AccountLogin.LoginStatus.Failed => StatusFlags.Failed,
                AccountLogin.LoginStatus.Complete => StatusFlags.Complete,
                _ => StatusFlags.None,
            }
        );
        await _loginTask;
    }

    private enum StatusFlags
    {
        None,
        Waiting,
        TokenReceived,
        Failed,
        Complete,
    }
}
