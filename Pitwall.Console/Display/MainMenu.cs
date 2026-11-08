using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>An entry on the launch screen menu.</summary>
/// <param name="Label">What the entry is called.</param>
/// <param name="Detail">A line explaining what it does, shown while it's selected.</param>
/// <param name="Activate">Runs when the entry is chosen with Enter.</param>
public sealed record MainMenuItem(string Label, string Detail, Func<Task> Activate);

/// <summary>
/// The launch screen menu, navigated with the arrow keys and Enter. Which entries are offered
/// depends on whether a session is running.
/// </summary>
public sealed class MainMenu(
    State state,
    ILiveTimingClient liveTimingClient,
    IJsonTimingClient jsonTimingClient,
    StartSimulatedSessionOptions replayOptions,
    TimingDataProcessor timingData,
    PitWallContext context,
    Formula1Account accountService,
    ITranscriptionProvider transcriptionProvider,
    SessionSchedule schedule
)
{
    /// <summary>Whether a live or recorded session has started sending data.</summary>
    public bool SessionRunning => state.SessionStarted || timingData.Latest.Lines.Count > 0;

    public IReadOnlyList<MainMenuItem> Items
    {
        get
        {
            var items = new List<MainMenuItem>();
            if (SessionRunning)
            {
                items.Add(
                    new(
                        "Open the pit wall",
                        context.Team is { } team
                            ? $"Back to the {team.Name} pit wall."
                            : "Follow a team's two cars; ▲ ▼ change team.",
                        () => Go(Screen.PitWallTiming)
                    )
                );
                items.Add(
                    new(
                        "Change team",
                        "Follow a different team's cars.",
                        () => Go(Screen.SelectTeam)
                    )
                );
            }

            // Only one session per run: once one has started its data can't be cleared, so a
            // second one would mix with it
            if (!SessionRunning)
            {
                // Named after the session on now, from F1's schedule, once it's been fetched
                var now = DateTimeOffset.UtcNow;
                var live = schedule.Current(now);
                var next = schedule.Next(now);
                items.Add(
                    new(
                        live is null
                            ? "Watch a live session"
                            : $"Watch {live.Location} {live.Name} live",
                        live is not null ? "Connect to the F1 live timing feed for this session."
                            : next is not null
                                ? $"No session on now. Next: {next.Location} {next.Name}, {next.Start.ToLocalTime():ddd d MMM HH:mm} your time."
                            : "Connect to the F1 live timing feed for the session happening now.",
                        async () =>
                        {
                            state.SessionStarted = true;
                            await liveTimingClient.StartAsync();
                            await Go(Screen.PitWallTiming);
                        }
                    )
                );
                foreach (var demo in DemoSession.All)
                {
                    items.Add(
                        new(
                            demo.Name,
                            demo.Description,
                            async () =>
                            {
                                state.SessionStarted = true;
                                _ = jsonTimingClient.LoadSimulationDataAsync(
                                    demo.Unpack(),
                                    demo.StartAt
                                );
                                await Go(Screen.PitWallTiming);
                            }
                        )
                    );
                }
                items.Add(
                    new(
                        "Replay a session",
                        "Play back a recorded or imported session. Download old ones with: pitwall import",
                        async () =>
                        {
                            replayOptions.Sessions =
                                await jsonTimingClient.GetDirectoryNamesAsync();
                            replayOptions.SelectedLocation = null;
                            await Go(Screen.StartSimulatedSession);
                        }
                    )
                );
            }
            items.Add(
                new(
                    "F1 TV account",
                    accountService.IsAuthenticated == Formula1Account.AuthenticationResult.Success
                        ? "Logged in. Manage your F1 TV account."
                        : "Log in to receive car telemetry and positions during live sessions.",
                    () => Go(Screen.ManageAccount)
                )
            );
            if (!transcriptionProvider.IsModelDownloaded)
            {
                items.Add(
                    new(
                        "Download radio transcription model",
                        "One-time 550 MB download. Team radio is then transcribed automatically, on this machine.",
                        () => Go(Screen.DownloadTranscriptionModel)
                    )
                );
            }
            items.Add(
                new(
                    "Settings",
                    "Folders, notifications, image drawing and more, saved to the config file.",
                    () => Go(Screen.Settings)
                )
            );
            items.Add(new("Quit", "Close Pitwall.", () => Go(Screen.Shutdown)));
            return items;
        }
    }

    /// <summary>The entry under the cursor, keeping the cursor within the menu.</summary>
    public int SelectedIndex
    {
        get
        {
            var count = Items.Count;
            if (state.CursorOffset >= count)
                state.CursorOffset = count - 1;
            return state.CursorOffset;
        }
    }

    private async Task Go(Screen screen)
    {
        if (screen != Screen.Shutdown)
            await Terminal.OutAsync(ControlSequences.ClearScreen(ClearMode.Full));
        state.CurrentScreen = screen;
        state.CursorOffset = 0;
    }
}
