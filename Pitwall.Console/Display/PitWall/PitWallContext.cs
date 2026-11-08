using Pitwall.Data;

namespace Pitwall.Console;

/// <summary>A team in the current session and its cars, best placed car first.</summary>
public sealed record PitWallTeam(string Name, string Colour, IReadOnlyList<string> DriverNumbers);

/// <summary>
/// Resolves the team selected for the pit wall pages and its cars from the live driver list.
/// </summary>
public sealed class PitWallContext(
    State state,
    DriverListProcessor driverList,
    TimingDataProcessor timingData
)
{
    /// <summary>All teams in the session, ordered by their best placed car.</summary>
    public IReadOnlyList<PitWallTeam> Teams =>
        driverList
            .Latest.Where(x => !string.IsNullOrWhiteSpace(x.Value.TeamName))
            .GroupBy(x => x.Value.TeamName!)
            .Select(group =>
            {
                var drivers = group.Select(x => x.Key).OrderBy(Position).ToList();
                var colour = group.First().Value.TeamColour ?? "FFFFFF";
                return new PitWallTeam(group.Key, colour, drivers);
            })
            .OrderBy(team => team.DriverNumbers.Select(Position).DefaultIfEmpty(99).Min())
            .ToList();

    /// <summary>
    /// The selected team. With none chosen yet, the first team in the list is chosen (and kept,
    /// so it doesn't change as the order does); <c>null</c> only before the session's teams are
    /// known, or if the chosen team isn't in this session.
    /// </summary>
    public PitWallTeam? Team
    {
        get
        {
            var teams = Teams;
            if (state.SelectedTeam is null && teams.Count > 0)
                state.SelectedTeam = teams[0].Name;
            return teams.FirstOrDefault(x => x.Name == state.SelectedTeam);
        }
    }

    public bool IsTeamCar(string driverNumber) =>
        Team?.DriverNumbers.Contains(driverNumber) ?? false;

    public DriverListDataPoint.Driver Driver(string? driverNumber) =>
        (driverNumber is null ? null : driverList.Latest.GetValueOrDefault(driverNumber)) ?? new();

    /// <summary>Selects the next (or with -1, previous) team in the list, wrapping around.</summary>
    public void CycleTeam(int direction = 1)
    {
        var teams = Teams;
        if (teams.Count == 0)
            return;
        var index = teams.ToList().FindIndex(x => x.Name == state.SelectedTeam);
        var next = (((index + direction) % teams.Count) + teams.Count) % teams.Count;
        state.SelectedTeam = teams[next].Name;
    }

    private int Position(string driverNumber) =>
        timingData.Latest.Lines.GetValueOrDefault(driverNumber)?.Line
        ?? driverList.Latest.GetValueOrDefault(driverNumber)?.Line
        ?? 99;
}
