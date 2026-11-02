using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pitwall.Data;

namespace Pitwall.Console.Tests;

/// <summary>The settings screen's logic, against a temporary config file.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Join(
        Path.GetTempPath(),
        $"pitwall-settings-{Guid.NewGuid():N}"
    );
    private readonly string _previousPath = ConfigFile.FilePath;
    private readonly ConsoleOptions _console = new();
    private readonly LiveTimingOptions _liveTiming = new();
    private readonly SettingsStore _store;

    public SettingsTests()
    {
        Directory.CreateDirectory(_directory);
        ConfigFile.FilePath = Path.Join(_directory, "config.json");
        _store = new SettingsStore(Options.Create(_console), Options.Create(_liveTiming));
    }

    public void Dispose()
    {
        ConfigFile.FilePath = _previousPath;
        Directory.Delete(_directory, recursive: true);
    }

    private Setting Find(string path) => _store.Settings.Single(x => x.Path == path);

    private JsonObject File() =>
        JsonNode.Parse(System.IO.File.ReadAllText(ConfigFile.FilePath))!.AsObject();

    [Fact]
    public async Task TogglingANotificationSavesItAndAppliesItNow()
    {
        var notify = Find("Notify");
        Assert.Equal("On", _store.Value(notify));

        Assert.Null(await _store.SaveAsync(notify, _store.Next(notify)));

        Assert.Equal("Off", _store.Value(notify));
        Assert.False(_liveTiming.Notify);
        Assert.False(File()["Notify"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RestartSettingsShowTheSavedValueAsPending()
    {
        var api = Find("ApiEnabled");

        await _store.SaveAsync(api, _store.Next(api));

        Assert.Equal("Off", _store.Value(api));
        Assert.Equal("On", _store.Pending(api));
        Assert.True(File()["ApiEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExistingSettingsAreKeptAndMatchedIgnoringCase()
    {
        System.IO.File.WriteAllText(
            ConfigFile.FilePath,
            """{ "notify": true, "formula1AccessToken": "abc" }"""
        );

        await _store.SaveAsync(Find("Notify"), "Off");

        var file = File();
        Assert.False(file["notify"]!.GetValue<bool>());
        Assert.Equal(2, file.Count);
        Assert.Equal("abc", file["formula1AccessToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task FoldersAreCreatedAndNestedSettingsWritten()
    {
        var folder = Path.Join(_directory, "data");
        Assert.Null(await _store.SaveAsync(Find("DataDirectory"), folder));
        Assert.True(Directory.Exists(folder));

        Assert.NotNull(
            await _store.SaveAsync(Find("ExternalPlayerSync:Url"), "http://not-a-websocket")
        );
        Assert.Null(
            await _store.SaveAsync(Find("ExternalPlayerSync:Url"), "ws://localhost:9090/jsonrpc")
        );
        Assert.Equal(
            "ws://localhost:9090/jsonrpc",
            File()["ExternalPlayerSync"]!["Url"]!.GetValue<string>()
        );
    }

    [Fact]
    public async Task ChoicesCycleAndAutomaticRemovesTheSetting()
    {
        var protocol = Find("ForceGraphicsProtocol");
        Assert.Equal("Automatic", _store.Value(protocol));

        await _store.SaveAsync(protocol, _store.Next(protocol));
        Assert.Equal("iTerm", File()["ForceGraphicsProtocol"]!.GetValue<string>());

        await _store.SaveAsync(protocol, "Automatic");
        Assert.Null(File()["ForceGraphicsProtocol"]);
    }
}
