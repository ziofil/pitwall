using System.Text.Json;
using System.Text.Json.Serialization;
using Pitwall.Console.Api;

namespace Pitwall.Console;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(MainDisplay.GitHubTagEntry))]
[JsonSerializable(typeof(MainDisplay.GitHubTagEntry[]))]
[JsonSerializable(typeof(ConsoleOptions))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(ControlError))]
internal partial class ConsoleSerializerContext : JsonSerializerContext
{
    public static ConsoleSerializerContext Pretty { get; } =
        new(
            new(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
                AllowTrailingCommas = true,
                WriteIndented = true,
            }
        );
}
