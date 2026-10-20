using System.Text.Json;

namespace TextHotKey.Tests.Unit;

// 설정/단축키 파일 저장·복원.
public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thk-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string HotkeysPath => Path.Combine(_dir, "hotkeys.json");

    [Fact]
    public void Settings_Defaults_WhenFileMissing()
    {
        var s = new SettingManager(SettingsPath);
        Assert.False(s.GetAutoUpdate());
        Assert.False(s.GetBetaOptIn());
        Assert.True(s.GetTheme()); // 기본 Dark
    }

    [Fact]
    public void Settings_RoundTrip_AcrossInstances()
    {
        var s = new SettingManager(SettingsPath);
        s.SetAutoUpdate(true);
        s.SetBetaOptIn(true);
        s.SetTheme("Light");
        s.Set("BetaEmail", "a@b.c");

        var reloaded = new SettingManager(SettingsPath);
        Assert.True(reloaded.GetAutoUpdate());
        Assert.True(reloaded.GetBetaOptIn());
        Assert.False(reloaded.GetTheme());
        Assert.Equal("a@b.c", reloaded.Get("BetaEmail", ""));
    }

    [Fact]
    public void Settings_WrongValueType_FallsBackToDefault()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"AutoUpdate\":\"not-a-bool\"}");
        Assert.False(new SettingManager(SettingsPath).GetAutoUpdate());
    }

    // 저장 도중 전원이 꺼지는 등으로 파일이 깨져도 앱이 시작은 되어야 한다(기본값으로).
    [Theory]
    [InlineData("")]
    [InlineData("{\"AutoUpdate\":tr")]
    [InlineData("\0\0\0\0")]
    [InlineData("[1,2,3]")]
    public void Settings_CorruptFile_FallsBackToDefaults_AndKeepsBackup(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, content);
        var s = new SettingManager(SettingsPath);
        Assert.False(s.GetAutoUpdate());

        // 깨진 원본은 지우지 않고 옆에 보존한다(수동 복구용).
        var backup = Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        Assert.Equal(content, File.ReadAllText(backup));

        // 이후 저장은 정상 동작한다.
        s.SetAutoUpdate(true);
        Assert.True(new SettingManager(SettingsPath).GetAutoUpdate());
    }

    [Fact]
    public void Settings_Save_LeavesNoTempFile()
    {
        var s = new SettingManager(SettingsPath);
        s.SetAutoUpdate(true);
        s.SetAutoUpdate(false);
        Assert.Equal(new[] { SettingsPath }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void Hotkeys_LoadsSavedList()
    {
        Directory.CreateDirectory(_dir);
        var items = new[] { new HotkeyItem { Hotkey = "Ctrl+Alt+A", Text = "안녕하세요" } };
        File.WriteAllText(HotkeysPath, JsonSerializer.Serialize(items));

        var m = new HotkeyManager(HotkeysPath);
        var item = Assert.Single(m.HotkeyList);
        Assert.Equal("Ctrl+Alt+A", item.Hotkey);
        Assert.Equal("안녕하세요", item.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[{\"Hotkey\":\"Ctrl+A\",")]
    [InlineData("{\"Hotkey\":\"Ctrl+A\"}")]
    public void Hotkeys_CorruptFile_StartsEmpty_AndKeepsBackup(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HotkeysPath, content);
        var m = new HotkeyManager(HotkeysPath);
        Assert.Empty(m.HotkeyList);
        var backup = Assert.Single(Directory.GetFiles(_dir, "hotkeys.json.corrupt-*"));
        Assert.Equal(content, File.ReadAllText(backup));
    }

    [Fact]
    public void Hotkeys_NullEntries_AreIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HotkeysPath, "[null,{\"Hotkey\":\"Ctrl+B\",\"Text\":\"x\"}]");
        var item = Assert.Single(new HotkeyManager(HotkeysPath).HotkeyList);
        Assert.Equal("Ctrl+B", item.Hotkey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 무시 */ }
    }
}
