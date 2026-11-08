using System.Text.Json.Nodes;

namespace Pitwall.Data;

/// <summary>
/// When the session went green: lights out in a race, the start of each part of qualifying, and
/// any restart. Noted as messages arrive, before the stream delay holds them back, so the pit
/// wall can be synced to a TV broadcast by marking the moment the start is seen on screen.
/// </summary>
public sealed class SessionStarts
{
    private readonly SortedSet<DateTimeOffset> _starts = [];

    public IReadOnlyList<DateTimeOffset> All
    {
        get
        {
            lock (_starts)
                return [.. _starts];
        }
    }

    /// <summary>Notes the starts in a feed message, if it's session data.</summary>
    public void Observe(string type, string? data)
    {
        if (type != "SessionData" || string.IsNullOrEmpty(data))
            return;
        try
        {
            var starts = Starts(JsonNode.Parse(data)).ToList();
            lock (_starts)
            {
                foreach (var start in starts)
                    _starts.Add(start);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not session data in a form we know; nothing to note
        }
    }

    /// <summary>The start closest to a moment of the session, if any are known.</summary>
    public DateTimeOffset? Nearest(DateTimeOffset to)
    {
        lock (_starts)
            return _starts.Count == 0 ? null : _starts.MinBy(x => (x - to).Duration());
    }

    /// <summary>
    /// The times the session status became <c>Started</c>, from a session data message: its
    /// status series is a list in the first snapshot and keyed by index in updates.
    /// </summary>
    internal static IEnumerable<DateTimeOffset> Starts(JsonNode? sessionData)
    {
        var series = sessionData?["StatusSeries"] switch
        {
            JsonArray array => array,
            JsonObject obj => obj.Select(x => x.Value),
            _ => [],
        };
        foreach (var entry in series)
        {
            if (
                entry?["SessionStatus"]?.GetValue<string>() == "Started"
                && entry["Utc"]?.GetValue<string>() is { } utc
                && DateTimeOffset.TryParse(
                    utc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var time
                )
            )
            {
                yield return time;
            }
        }
    }
}
