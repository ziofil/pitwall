using Pitwall.Data;
using Spectre.Console;

namespace Pitwall.Console;

public static partial class CommandHandler
{
    public static async Task LoginToFormula1Account(bool? isVerbose)
    {
        var builder = GetBuilder(
            isVerbose: isVerbose,
            useConsoleLogging: isVerbose.GetValueOrDefault()
        );

        var app = builder.Build();

        await EnsureConfigFileExistsAsync(app.Logger);

        var accountService = app.Services.GetRequiredService<Formula1Account>();
        var existingPayload = accountService.Payload;

        // Allow a relogin on the day of expiry but not before
        if (existingPayload is not null && existingPayload.Expiry.Date >= DateTime.Today)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"""
                An access token is already configured in [bold]{ConsoleOptions.ConfigFilePath}[/].
                [dim]{existingPayload}[/]
                This token will expire on [bold]{existingPayload.Expiry:yyyy-MM-dd}[/], at which point you'll need to login again.
                If you'd like to log in again, please first logout using [bold]pitwall logout[/].
                """
            );
            return;
        }

        var preamble = $"""
            Log in to your Formula 1 account (with any level of F1 TV subscription) to receive all the live timing feeds.

            An account is [bold]not[/] needed to use Pitwall. During live sessions it adds:
            - car positions on the Track page
            - car telemetry on the Telemetry page
            - pit stop times

            Replays of finished sessions include all of this without logging in.

            Once logged in, your access token will be stored in [bold]{ConsoleOptions.ConfigFilePath}[/]. Your actual account credentials will not be stored anywhere.
            Simply remove the token entry from this file, or run [bold]pitwall logout[/] to remove usage of your token.
            """;

        AnsiConsole.MarkupLine(preamble);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Proceed to login?", defaultValue: false))
        {
            return;
        }

        AnsiConsole.MarkupLine(
            "Opening a browser window for you to login to your Formula 1 account. Once logged in, close the browser window and return here."
        );

        AnsiConsole.WriteLine();

        var accountLogin = app.Services.GetRequiredService<AccountLogin>();
        var payload = await accountLogin.LoginAsync(
            (status) =>
            {
                AnsiConsole.WriteLine();
                switch (status)
                {
                    case AccountLogin.LoginStatus.TokenReceived:
                        AnsiConsole.MarkupLine(
                            """
                            [bold green]Received login cookie, you may now close the browser.[/]
                            """
                        );
                        break;
                    case AccountLogin.LoginStatus.Failed:
                        AnsiConsole.MarkupLine(
                            """
                            [bold red]No or invalid token received from login.[/]
                            """
                        );
                        break;
                }
            }
        );
        if (payload is not null)
        {
            AnsiConsole.MarkupLine(
                $"""
                [green]Login Successful.[/] Your access token has been saved in [bold]{ConsoleOptions.ConfigFilePath}[/].
                This token will expire on [bold]{payload.Expiry:yyyy-MM-dd}[/], at which point you'll need to login again.
                """
            );
        }
    }

    public static async Task LogoutOfFormula1Account()
    {
        AnsiConsole.MarkupLine(
            $"""
            Logging out will remove your access token stored in [bold]{ConsoleOptions.ConfigFilePath}[/].
            To log back in again in the future, simply run [bold]pitwall login[/].
            """
        );
        AnsiConsole.WriteLine();

        var builder = GetBuilder();

        var app = builder.Build();

        var accountLogin = app.Services.GetRequiredService<AccountLogin>();
        await accountLogin.LogoutAsync();

        AnsiConsole.MarkupLine(
            $"""
            [green]Logout successful.[/]
            """
        );
    }
}
