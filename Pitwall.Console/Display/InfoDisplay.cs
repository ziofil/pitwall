using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Pitwall.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

public sealed class InfoDisplay(
    TerminalInfoProvider terminalInfo,
    Formula1Account accountService,
    IOptions<ConsoleOptions> options
) : IDisplay
{
    public Screen Screen => Screen.Info;

    public Task<IRenderable> GetContentAsync()
    {
        var content = $"""
            [bold]Configuration[/]
            [bold]Data Directory:[/]        {options.Value.DataDirectory}
            [bold]Log Directory:[/]         {options.Value.LogDirectory}
            [bold]Audible Notifications:[/] {options.Value.Notify}
            [bold]Verbose Mode:[/]          {options.Value.Verbose}
            [bold]Forced Protocol:[/]       {options.Value.ForceGraphicsProtocol?.ToString() ?? "None"}
            [bold]Prevent Display Sleep:[/] {options.Value.PreventDisplaySleep}
            [bold]F1 TV Account:[/]         {accountService.IsAuthenticated}
            [bold]Config Override File:[/]  {File.Exists(
                ConsoleOptions.ConfigFilePath
            )} ({ConsoleOptions.ConfigFilePath})
            See https://github.com/ziofil/pitwall#configuration for information on how to configure these options.
            [dim]{Markup.Escape(accountService.Payload?.ToString() ?? "")}[/]

            [bold]Terminal Diagnostics[/]
            [bold]TERM_PROGRAM:[/]        {Environment.GetEnvironmentVariable("TERM_PROGRAM")}
            [bold]Window Size W/H:[/]     {terminalInfo.TerminalSize.Value.Width}/{terminalInfo.TerminalSize.Value.Height} ({terminalInfo.TerminalSize.Value.Height / Terminal.Size.Height})
            [bold]iTerm2 Graphics:[/]     {terminalInfo.IsITerm2ProtocolSupported.Value}
            [bold]Kitty Graphics:[/]      {terminalInfo.IsKittyProtocolSupported.Value}
            [bold]Sixel Graphics:[/]      {terminalInfo.IsSixelSupported.Value}
            [bold]Synchronized Output:[/] {terminalInfo.IsSynchronizedOutputSupported.Value}
            [bold]Version:[/]             {ThisAssembly.AssemblyInformationalVersion}
            [bold]Runtime Identifier:[/]  {RuntimeInformation.RuntimeIdentifier}
            
            [bold]OS:[/] {RuntimeInformation.OSDescription}
            """;

        return Task.FromResult<IRenderable>(new Panel(new Markup(content)).Expand());
    }
}
