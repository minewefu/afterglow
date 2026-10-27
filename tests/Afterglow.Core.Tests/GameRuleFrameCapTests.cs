using System.Text.Json;
using System.Text.Json.Serialization;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

/// <summary>
/// R1, the per-game frame cap: empty = leave the cap to the driver, 0 = off for
/// this game, N = a cap. Settings files from before this change stored 0 for both
/// an empty field and a typed 0, and what those rules did in 1.5.0 was leave the
/// cap to the driver, so a legacy 0 reads as empty. The file stays readable both
/// ways: an older build reading a newer file is modelled by
/// <see cref="LegacySettings"/>, the 1.5.0 shape of the record, read with
/// SettingsStore's options.
/// </summary>
[Collection("AppPaths")]
public sealed class GameRuleFrameCapTests : IDisposable
{
    private readonly string _root;
    private readonly string? _before;

    public GameRuleFrameCapTests()
    {
        _before = AppPaths.OverrideRoot;
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-framecap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = _before;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>SettingsStore's own options (camelCase, enums as strings), as every build since 1.0 reads the file.</summary>
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>1.5.0's GameRule, field for field.</summary>
    private sealed record LegacyGameRule
    {
        public required string ExecutableName { get; init; }

        public required string ProfileName { get; init; }

        public bool RevertOnExit { get; init; } = true;

        public int FrameCapFps { get; init; }

        public string DriverVsync { get; init; } = "default";

        public bool LowLatency { get; init; }
    }

    private sealed record LegacySettings
    {
        public int PollingIntervalMs { get; init; } = 1000;

        public IReadOnlyList<LegacyGameRule> GameRules { get; init; } = [];
    }

    private static AppSettings LoadFrom(string json)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        return SettingsStore.Load();
    }

    private static GameRule Rule(string exe, int? cap) => new() { ExecutableName = exe, ProfileName = "p", FrameCapFps = cap };

    [Fact]
    public void A_legacy_zero_reads_as_empty_and_a_legacy_cap_as_that_cap()
    {
        // As 1.5.0 wrote them: an empty field and a typed 0 are both 0.
        var settings = LoadFrom("""
            { "gameRules": [
                { "executableName": "a.exe", "profileName": "p", "frameCapFps": 0, "driverVsync": "on" },
                { "executableName": "b.exe", "profileName": "p", "frameCapFps": 0 },
                { "executableName": "c.exe", "profileName": "p", "frameCapFps": 141 } ] }
            """);

        int?[] caps = [.. settings.GameRules.Select(r => r.FrameCapFps)];
        Assert.Equal([null, null, 141], caps);

        // 1.5.0 deleted the cap for a 0 (the game inherited the global cap); the
        // migrated rule writes no cap at all, so that is still what it gets.
        var driver = settings.GameRules[0].ToDriverSettings();
        Assert.Null(driver.FrameCapFps);
        Assert.True(driver.AnythingSet);   // vsync is still written
        Assert.False(settings.GameRules[1].ToDriverSettings().AnythingSet);
    }

    [Fact]
    public void Empty_off_and_a_cap_survive_a_save_and_load()
    {
        SettingsStore.Save(new AppSettings { GameRules = [Rule("a.exe", null), Rule("b.exe", 0), Rule("c.exe", 60)] });

        var loaded = SettingsStore.Load().GameRules;

        int?[] caps = [.. loaded.Select(r => r.FrameCapFps)];
        Assert.Equal([null, 0, 60], caps);
        Assert.Equal(Rule("b.exe", 0), loaded[1]);   // record equality: nothing else changed
    }

    [Fact]
    public void An_older_build_reads_a_new_file_and_sees_its_own_meaning()
    {
        SettingsStore.Save(new AppSettings { GameRules = [Rule("a.exe", null), Rule("b.exe", 0), Rule("c.exe", 60)] });
        string json = File.ReadAllText(AppPaths.SettingsFile);

        // No exception: the extra "frameCapOff" is an unknown member, which
        // System.Text.Json skips by default, and "frameCapFps" is still an int.
        var old = JsonSerializer.Deserialize<LegacySettings>(json, StoreOptions)!;

        // 1.5.0's own reading: the cap where there is one, 0 ("no cap") otherwise.
        int[] caps = [.. old.GameRules.Select(r => r.FrameCapFps)];
        Assert.Equal([0, 0, 60], caps);
    }

    [Fact]
    public void A_rule_saved_again_by_an_older_build_keeps_its_cap_and_off_becomes_empty()
    {
        // The older build drops the flag it doesn't know when it saves: "off"
        // falls back to the legacy reading (empty), never the other way, and a
        // real cap survives the round trip.
        SettingsStore.Save(new AppSettings { GameRules = [Rule("b.exe", 0), Rule("c.exe", 60)] });
        var old = JsonSerializer.Deserialize<LegacySettings>(File.ReadAllText(AppPaths.SettingsFile), StoreOptions)!;
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(old, StoreOptions));

        int?[] caps = [.. SettingsStore.Load().GameRules.Select(r => r.FrameCapFps)];

        Assert.Equal([null, 60], caps);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("0", 0)]
    [InlineData("off", 0)]
    [InlineData(" OFF ", 0)]
    [InlineData("60", 60)]
    [InlineData("1000", 1000)]
    public void The_editor_field_parses_empty_off_and_caps(string text, int? expected)
    {
        Assert.Null(GameRule.ParseFrameCap(text, out int? fps));
        Assert.Equal(expected, fps);
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("-5")]
    [InlineData("60.5")]
    [InlineData("sixty")]
    [InlineData("+60")]
    public void The_editor_field_refuses_anything_else(string text)
    {
        // These used to be stored silently: unparsable text as 0, a number
        // outside 0–1000 clamped.
        string? error = GameRule.ParseFrameCap(text, out int? fps);

        Assert.NotNull(error);
        Assert.Contains(text.Trim(), error, StringComparison.Ordinal);
        Assert.Null(fps);
    }

    [Fact]
    public void A_cap_of_zero_alone_is_a_driver_option_to_write()
    {
        // It used to count as nothing set, so a rule whose only driver option
        // was "off" wrote nothing and a global cap still capped the game.
        Assert.True(Rule("a.exe", 0).ToDriverSettings().AnythingSet);
        Assert.False(Rule("a.exe", null).ToDriverSettings().AnythingSet);
    }
}
