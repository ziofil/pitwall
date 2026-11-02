using System.Text.Json.Nodes;

namespace Pitwall.Console;

/// <summary>
/// Reads and changes Pitwall's <c>config.json</c> in place: only the settings being changed are
/// touched, and anything else in the file (including keys Pitwall doesn't know) is kept.
/// </summary>
public static class ConfigFile
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>The file changed: Pitwall's config file, or another one in tests.</summary>
    internal static string FilePath { get; set; } = ConsoleOptions.ConfigFilePath;

    /// <summary>Sets a setting, by its path such as <c>Notify</c> or <c>ExternalPlayerSync:Url</c>.</summary>
    /// <param name="value">The new value, or <c>null</c> to remove the setting and use the default.</param>
    public static async Task SetAsync(string path, JsonNode? value)
    {
        await Lock.WaitAsync();
        try
        {
            var root = await ReadAsync();
            var parts = path.Split(':');
            var parent = root;
            foreach (var part in parts[..^1])
            {
                var key = FindKey(parent, part) ?? part;
                if (parent[key] is not JsonObject child)
                {
                    child = [];
                    parent[key] = child;
                }
                parent = child;
            }

            // Keys are matched ignoring case, as the settings themselves are, so a hand-written
            // "notify" is replaced rather than duplicated
            var existing = FindKey(parent, parts[^1]);
            if (existing is not null)
                parent.Remove(existing);
            if (value is not null)
                parent[existing ?? parts[^1]] = value;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(
                FilePath,
                root.ToJsonString(ConsoleSerializerContext.Pretty.Options)
            );
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The config file's contents, or an empty object if there's no file yet.</summary>
    public static async Task<JsonObject> ReadAsync()
    {
        if (!File.Exists(FilePath))
            return [];
        var contents = await File.ReadAllTextAsync(FilePath);
        return string.IsNullOrWhiteSpace(contents)
            ? []
            : JsonNode.Parse(contents, new() { PropertyNameCaseInsensitive = true })?.AsObject()
                ?? throw new InvalidOperationException("Unable to parse config JSON");
    }

    private static string? FindKey(JsonObject node, string key) =>
        node.Select(x => x.Key)
            .FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
}
