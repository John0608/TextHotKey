namespace TextHotKey.Tests.Unit;

public class HotkeyParseTests
{
    private const uint Alt = 0x0001, Ctrl = 0x0002, Shift = 0x0004;

    private static HotkeyManager NewManager() =>
        new(Path.Combine(Path.GetTempPath(), "thk-tests", Guid.NewGuid().ToString("N"), "hotkeys.json"));

    [Theory]
    [InlineData("Ctrl+A", Ctrl, 0x41u)]
    [InlineData("Ctrl+Alt+F1", Ctrl | Alt, 0x70u)]
    [InlineData("Shift+Ctrl+D1", Ctrl | Shift, 0x31u)]
    [InlineData("ctrl + shift + z", Ctrl | Shift, 0x5Au)]
    [InlineData("Alt+NumPad5", Alt, 0x65u)]
    public void ParseHotkey_ParsesModifiersAndKey(string hotkey, uint modifiers, uint vk)
    {
        Assert.Equal((modifiers, vk), NewManager().ParseHotkey(hotkey));
    }

    [Fact]
    public void ParseHotkey_UnknownKey_GivesZeroVk()
    {
        Assert.Equal((Ctrl, 0u), NewManager().ParseHotkey("Ctrl+NotAKey"));
    }
}
