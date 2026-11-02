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
}
