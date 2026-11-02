using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public sealed class ManageAccountDisplay(
    State state,
    Formula1Account accountService,
    AccountLogin accountLogin
) : IDisplay
{
    public Screen Screen => Screen.ManageAccount;

    public Task<IRenderable> GetContentAsync()
    {
        var markupText = accountService.IsAuthenticated switch
        {
            Formula1Account.AuthenticationResult.Success => $"""
                [green]You're logged in to your F1 TV account.[/]

                The access token is stored in [bold]{Markup.Escape(
                    ConsoleOptions.ConfigFilePath
                )}[/].
                It expires on [bold]{accountService.Payload?.Expiry:yyyy-MM-dd}[/], after which you'll need to log in again.
                Log in again if you've changed your password or the account has changed.

                {LoggedInOptions()}
                """,
            Formula1Account.AuthenticationResult.NoToken => $"""
                Log in to your Formula 1 account (with any level of F1 TV subscription) to receive all the live timing feeds.

                An account is [bold]not[/] needed to use Pitwall. During live sessions it adds:
                - car positions on the Track page
                - car telemetry on the Telemetry page
                - pit stop times

                Replays of finished sessions include all of this without logging in.

                Once logged in, your access token is stored in [bold]{Markup.Escape(
                    ConsoleOptions.ConfigFilePath
                )}[/]. Your account credentials are not stored anywhere.
                Simply remove the token entry from this file, or run [bold]pitwall logout[/] to remove usage of your token.

                See https://github.com/ziofil/pitwall#f1-tv-account-login for details


                Press [bold]Enter[/] to open a browser to login to your F1 TV account.
                """,
            Formula1Account.AuthenticationResult.InvalidToken => """
                The stored token is invalid. Press [bold]Enter[/] to log in again.
                """,
            Formula1Account.AuthenticationResult.InvalidSubscriptionStatus => $"""
                Your Formula 1 Access Token does not have an active (paid) subscription status.
                If you've recently subscribed, you may want to try logging in again.

                Press [bold]Enter[/] to log in again.
                """,
            Formula1Account.AuthenticationResult.ExpiredToken => $"""
                [yellow]Formula 1 account token has expired![/]
                Please press [bold]Enter[/] to open a browser window to log back in
                """,
            _ => "Unknown error",
        };
        if (
            accountLogin.WindowUsed
            && accountService.IsAuthenticated != Formula1Account.AuthenticationResult.Success
        )
        {
            markupText +=
                "\n\n[yellow]The login window can only be opened once per run. Restart Pitwall to log in again.[/]";
        }

        return Task.FromResult<IRenderable>(new Panel(new Markup(markupText)).Expand());
    }

    /// <summary>The logged-in options, with the one under the cursor highlighted.</summary>
    private string LoggedInOptions()
    {
        var options = ManageAccountInputHandler.LoggedInOptions;
        if (state.CursorOffset >= options.Length)
            state.CursorOffset = options.Length - 1;

        return string.Join(
            "\n",
            options.Select(
                (option, i) =>
                    option == "Log in again" && accountLogin.WindowUsed
                        ? $"  [grey]{option} (restart Pitwall first: the login window opens once per run)[/]"
                    : i == state.CursorOffset ? $"[#e10600]▌[/][bold black on white] {option} [/]"
                    : $"  {option}"
            )
        );
    }
}
