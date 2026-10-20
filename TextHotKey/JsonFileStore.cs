using System.IO;
using System.Text.Json;

namespace TextHotKey
{
    // 설정/단축키 JSON 파일 읽기·쓰기.
    //  - 쓰기: 임시 파일에 쓴 뒤 교체(원자적) → 저장 도중 꺼져도 기존 파일이 반쯤 쓰인 채로 남지 않는다.
    //  - 읽기: 파일이 깨졌으면 앱이 죽지 않도록 기본값을 돌려주고, 깨진 파일은 *.corrupt-시각 으로 옮겨 보존한다.
    internal static class JsonFileStore
    {
        public static T? Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                Logger.Error($"Corrupt file {path}: {ex.Message}. Moved to {backup}, using defaults.");
                try { File.Move(path, backup, overwrite: true); } catch { /* 보존 실패해도 기본값으로 계속 */ }
                return null;
            }
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
