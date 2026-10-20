using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TextHotKey.Tests.Infrastructure;

// 테스트 1건용 격리 환경: 임시 설치 폴더 + 데이터/임시 폴더(THK_TEST_ROOT).
// Dispose 시 이 폴더에서 실행된 프로세스를 모두 종료하고 정리한다.
internal sealed class InstallSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "thk-e2e", Guid.NewGuid().ToString("N")[..8]);
    public string InstallDir => Path.Combine(Root, "install");
    public string DataDir => Path.Combine(Root, "data");
    public string UpdateTempDir => Path.Combine(Root, "update-temp");
    public string ExePath => Path.Combine(InstallDir, "TextHotKey.exe");
    public string AppLogPath => Path.Combine(Root, "app.log");
    public string UpdaterLogPath => Path.Combine(UpdateTempDir, "updater.log");

    public InstallSandbox()
    {
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UpdateTempDir);
    }

    // 릴리스 zip을 설치 폴더에 풀어 "설치된 상태"를 만든다(설치 마법사가 하는 것과 같은 결과).
    public void Install(string zip) => ZipFile.ExtractToDirectory(zip, InstallDir, overwriteFiles: true);

    public void WriteSettings(string json) => File.WriteAllText(Path.Combine(DataDir, "settings.json"), json);

    public string? InstalledFileVersion() =>
        File.Exists(ExePath) ? FileVersionInfo.GetVersionInfo(ExePath).FileVersion : null;

    public string ReadAppLog() => ReadShared(AppLogPath);
    public string ReadUpdaterLog() => ReadShared(UpdaterLogPath);

    public void ApplyEnv(ProcessStartInfo psi, string? updateApi = null)
    {
        psi.Environment["THK_TEST_ROOT"] = Root;
        if (updateApi != null) psi.Environment["THK_TEST_UPDATE_API"] = updateApi;
    }

    public Process LaunchApp(string? updateApi = null)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = InstallDir };
        ApplyEnv(psi, updateApi);
        return Process.Start(psi)!;
    }

    // 설치 폴더의 exe로 실행 중인 TextHotKey 프로세스들.
    public List<Process> RunningApps() =>
        Process.GetProcessesByName("TextHotKey").Where(p => IsUnder(p, InstallDir)).ToList();

    public static Dictionary<string, string> HashTree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))),
            StringComparer.OrdinalIgnoreCase);

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout, int pollMs = 250)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (condition()) return true; } catch { /* 일시적 오류(파일 잠김 등)는 재시도 */ }
            Thread.Sleep(pollMs);
        }
        try { return condition(); } catch { return false; }
    }

    private static bool IsUnder(Process p, string dir)
    {
        try
        {
            var path = p.MainModule?.FileName;
            return path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        foreach (var name in new[] { "TextHotKey", "Updater" })
            foreach (var p in Process.GetProcessesByName(name).Where(p => IsUnder(p, Root)))
                try { p.Kill(true); p.WaitForExit(5000); } catch { /* 무시 */ }

        for (int i = 0; i < 10 && Directory.Exists(Root); i++)
        {
            try { Directory.Delete(Root, true); }
            catch { Thread.Sleep(300); }
        }
    }
}
