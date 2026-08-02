using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Interop;

namespace TextHotKey
{
    public class HotkeyManager
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CTRL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        private static readonly string SaveHotKeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TextHotKey", "hotkeys.json");

        public List<HotkeyItem> HotkeyList { get; private set; } = new List<HotkeyItem>();

        public HotkeyManager()
        {
            LoadHotkeys();
        }

        public void RegisterAll(IntPtr handle)
        {
            for (int i = 0; i < HotkeyList.Count; i++)
            {
                var (modifiers, vk) = ParseHotkey(HotkeyList[i].Hotkey);
                if (vk != 0)
                    RegisterHotKey(handle, i, modifiers, vk);
            }
        }

        public void UnregisterAll(IntPtr handle)
        {
            for (int i = 0; i < HotkeyList.Count; i++)
                UnregisterHotKey(handle, i);
        }

        public void Add(IntPtr handle, HotkeyItem item)
        {
            UnregisterAll(handle);
            HotkeyList.Add(item);
            SaveHotkeys();
            RegisterAll(handle);
        }

        public void Remove(IntPtr handle, HotkeyItem item)
        {
            UnregisterAll(handle);
            HotkeyList.Remove(item);
            SaveHotkeys();
            RegisterAll(handle);
        }

        private void LoadHotkeys()
        {
            if (!File.Exists(SaveHotKeyPath)) return;

            var json = File.ReadAllText(SaveHotKeyPath);
            var list = JsonSerializer.Deserialize<List<HotkeyItem>>(json);
            if (list != null)
            {
                HotkeyList.Clear();
                HotkeyList.AddRange(list);
            }
        }

        private void SaveHotkeys()
        {
            var dir = Path.GetDirectoryName(SaveHotKeyPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(HotkeyList);
            File.WriteAllText(SaveHotKeyPath, json);
        }

        public (uint modifiers, uint vk) ParseHotkey(string hotkey)
        {
            uint modifiers = 0;
            uint vk = 0;
            var parts = hotkey.Split('+');

            foreach (var part in parts)
            {
                switch (part.Trim().ToUpper())
                {
                    case "CTRL": modifiers |= MOD_CTRL; break;
                    case "ALT": modifiers |= MOD_ALT; break;
                    case "SHIFT": modifiers |= MOD_SHIFT; break;
                    default:
                        if (Enum.TryParse<Key>(part.Trim(), true, out var key))
                            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                        break;
                }
            }
            return (modifiers, vk);
        }
    }

    public class HotkeyItem
    {
        public string Hotkey { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}