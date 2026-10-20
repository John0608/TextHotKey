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
        // 키를 꾹 누를 때 자동 반복으로 단축키가 연달아 실행되지 않게 한다(새로 누를 때만 실행).
        private const uint MOD_NOREPEAT = 0x4000;

        private readonly string SaveHotKeyPath;

        public List<HotkeyItem> HotkeyList { get; private set; } = new List<HotkeyItem>();

        public HotkeyManager() : this(Path.Combine(AppEnv.DataDir, "hotkeys.json")) { }

        // 테스트에서 저장 위치를 지정할 때 사용.
        internal HotkeyManager(string savePath)
        {
            SaveHotKeyPath = savePath;
            LoadHotkeys();
        }

        public void RegisterAll(IntPtr handle)
        {
            for (int i = 0; i < HotkeyList.Count; i++)
            {
                var (modifiers, vk) = ParseHotkey(HotkeyList[i].Hotkey);
                if (vk != 0)
                    RegisterHotKey(handle, i, modifiers | MOD_NOREPEAT, vk);
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
            var list = JsonFileStore.Read<List<HotkeyItem?>>(SaveHotKeyPath);
            if (list != null)
            {
                HotkeyList.Clear();
                HotkeyList.AddRange(list.Where(i => i != null)!);
            }
        }

        private void SaveHotkeys()
        {
            JsonFileStore.Write(SaveHotKeyPath, HotkeyList);
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