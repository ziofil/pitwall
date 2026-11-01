using System.Text.Json.Nodes;

namespace Pitwall.Data;

public record struct RawTimingDataPoint(string Type, JsonNode Json, DateTimeOffset DateTime);
