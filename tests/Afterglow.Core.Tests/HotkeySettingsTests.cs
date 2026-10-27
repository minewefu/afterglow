using Afterglow.Core.Hotkeys;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

[Collection("AppPaths")]
public sealed class HotkeySettingsTests
{
    private static AppSettings LoadFrom(string json)
    {
        string? before = AppPaths.OverrideRoot;
        string root = Path.Combine(Path.GetTempPath(), "afterglow-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AppPaths.OverrideRoot = root;
            File.WriteAllText(AppPaths.SettingsFile, json);
            return SettingsStore.Load();
        }
        finally
        {
            AppPaths.OverrideRoot = before;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_settings_file_from_before_configurable_shortcuts_gets_the_defaults()
    {
        var settings = LoadFrom("""{ "pollingIntervalMs": 500, "closeToTray": true }""");

        Assert.True(settings.Hotkeys.Enabled);
        Assert.All(HotkeyActions.All, action => Assert.Null(settings.Hotkeys.For(action)));
    }

    [Fact]
    public void A_hand_edited_null_section_is_the_defaults_too()
    {
        var settings = LoadFrom("""{ "hotkeys": null }""");

        Assert.NotNull(settings.Hotkeys);
        Assert.True(settings.Hotkeys.Enabled);
    }

    [Fact]
    public void Chosen_none_and_default_survive_a_save_and_load()
    {
        var hotkeys = new HotkeySettings { Enabled = false }
            .With(HotkeyAction.PanicReset, "Ctrl+Shift+F9")
            .With(HotkeyAction.ToggleOverlay, string.Empty)
            .With(HotkeyAction.ApplyProfile3, "Alt+Shift+F3");

        string? before = AppPaths.OverrideRoot;
        string root = Path.Combine(Path.GetTempPath(), "afterglow-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AppPaths.OverrideRoot = root;
            SettingsStore.Save(new AppSettings { Hotkeys = hotkeys });
            var loaded = SettingsStore.Load().Hotkeys;

            Assert.False(loaded.Enabled);
            Assert.Equal("Ctrl+Shift+F9", loaded.For(HotkeyAction.PanicReset));
            Assert.Equal(string.Empty, loaded.For(HotkeyAction.ToggleOverlay));
            Assert.Equal("Alt+Shift+F3", loaded.For(HotkeyAction.ApplyProfile3));
            Assert.Null(loaded.For(HotkeyAction.ApplyProfile1));
            Assert.Null(loaded.For(HotkeyAction.ApplyProfile5));
        }
        finally
        {
            AppPaths.OverrideRoot = before;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_short_or_odd_slot_list_reads_as_defaults_past_its_end()
    {
        var settings = LoadFrom("""{ "hotkeys": { "profileSlots": [ " Ctrl+Shift+F1 ", null ] } }""");

        Assert.Equal("Ctrl+Shift+F1", settings.Hotkeys.For(HotkeyAction.ApplyProfile1));
        Assert.Null(settings.Hotkeys.For(HotkeyAction.ApplyProfile2));
        Assert.Null(settings.Hotkeys.For(HotkeyAction.ApplyProfile5));
        Assert.Equal("Ctrl+Shift+F5", settings.Hotkeys.With(HotkeyAction.ApplyProfile5, "Ctrl+Shift+F5").For(HotkeyAction.ApplyProfile5));
    }
}
