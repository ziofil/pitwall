namespace Pitwall.Console;

public record State
{
    public Screen PreviousScreen = Screen.Main;

    public Screen CurrentScreen
    {
        get;
        set
        {
            PreviousScreen = field;
            field = value;
        }
    } = Screen.Main;

    public int CursorOffset { get; set; } = 0;

    /// <summary>
    /// The team whose pit wall is being shown, as the team name from the driver list.
    /// </summary>
    public string? SelectedTeam { get; set; }

    /// <summary>
    /// Whether a live or recorded session has been started. Only one session can run per run of
    /// the app: the loaded data can't be cleared to start another.
    /// </summary>
    public bool SessionStarted { get; set; }

    /// <summary>When the stream delay was last changed, so it can be highlighted for a moment.</summary>
    public DateTimeOffset DelayChangedAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Formats a delay compactly: <c>0:45</c>, <c>1:02:30</c>, or with days for replays of old
    /// sessions, e.g. <c>849d 18:23:43</c>.
    /// </summary>
    public static string FormatDelay(TimeSpan delay) =>
        delay.TotalDays >= 1 ? $@"{(int)delay.TotalDays}d {delay:hh\:mm\:ss}"
        : delay.TotalHours >= 1 ? $@"{(int)delay.TotalHours}:{delay:mm\:ss}"
        : $@"{(int)delay.TotalMinutes}:{delay:ss}";
}
