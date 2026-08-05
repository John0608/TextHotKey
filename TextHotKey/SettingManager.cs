using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TextHotKey
{
    class SettingManager
    {
        private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextHotKey", "settings.json");

        private Dictionary<string, object> _settings = new();

        public SettingManager()
        {
            Load();
        }

        public void SetAutoUpdate(bool status)
        {
            Set("AutoUpdate", status);
        }

        public bool GetAutoUpdate()
        {
            return Get("AutoUpdate", false);
        }

        // 테스트(베타) 버전 받기 옵션(승인된 기기에서만 실제 적용).
        public void SetBetaOptIn(bool status)
        {
            Set("BetaOptIn", status);
        }

        public bool GetBetaOptIn()
        {
            return Get("BetaOptIn", false);
        }

        // 이 기기에서 베타 신청을 보냈는지(= 승인 대기중 판별용). 기기 코드와 같은 수명.
        public void SetBetaRequested(bool status)
        {
            Set("BetaRequested", status);
        }

        public bool GetBetaRequested()
        {
            return Get("BetaRequested", false);
        }

        public void SetTheme(string theme)
        {
            Set("Theme", theme);
        }

        public bool GetTheme()
        {
            return Get("Theme", "Dark") == "Dark";
        }

        public T Get<T>(string key, T defaultValue)
        {
            if (_settings.TryGetValue(key, out var value))
            {
                try
                {
                    if (value is JsonElement element)
                        return element.Deserialize<T>() ?? defaultValue;
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch { }
            }
            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _settings[key] = value!;
            Save();
        }

        private void Load()
        {
            if (!File.Exists(SavePath)) return;
            var json = File.ReadAllText(SavePath);
            _settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
        }

        private void Save()
        {
            var dir = Path.GetDirectoryName(SavePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(_settings));
        }

    }
}
