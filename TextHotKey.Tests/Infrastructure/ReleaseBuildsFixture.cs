using System.Diagnostics;

namespace TextHotKey.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public class E2ECollection : ICollectionFixture<ReleaseBuildsFixture> { public const string Name = "E2E"; }

// 실제 릴리스 스크립트(build-release.ps1)로 "구버전"과 "신버전" 패키지를 빌드한다.
// 업데이트 E2E 테스트는 구버전을 설치한 상태에서 신버전 zip으로 업데이트되는지를 본다.
// THK_E2E_REUSE=1 이면 이미 빌드된 산출물을 재사용한다(반복 실행 시간 단축).
public sealed class ReleaseBuildsFixture
{
    public const string OldVersion = "0.0.1";
    public const string NewVersion = "0.0.2";

    public string RepoRoot { get; }
    public string OldZip => Zip(OldVersion);
    public string NewZip => Zip(NewVersion);
    public string OldAppDir => AppDir(OldVersion);
    public string NewAppDir => AppDir(NewVersion);

    private string Root => Path.Combine(RepoRoot, "publish-e2e");
    private string Zip(string v) => Path.Combine(Root, v, "TextHotKey-win-x64.zip");
    private string AppDir(string v) => Path.Combine(Root, v, "app");

    public ReleaseBuildsFixture()
    {
        RepoRoot = FindRepoRoot();
        var reuse = Environment.GetEnvironmentVariable("THK_E2E_REUSE") == "1";
        foreach (var v in new[] { OldVersion, NewVersion })
        {
            if (reuse && File.Exists(Zip(v))) continue;
            Build(v);
        }
    }

    private void Build(string version)
    {
        var shell = FindOnPath("pwsh.exe") ?? "powershell.exe";
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoRoot,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepoRoot, "build-release.ps1"),
                                  "-Version", version, "-OutDir", Path.Combine(Root, version), "-SkipInstaller" })
            psi.ArgumentList.Add(a);

        // dotnet test가 설정한 MSBuild 환경변수가 자식 dotnet publish를 오염시키지 않도록 제거.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            p.Kill(true);
            throw new TimeoutException($"build-release.ps1 ({version}) 10분 초과");
        }
        if (p.ExitCode != 0 || !File.Exists(Zip(version)))
            throw new InvalidOperationException(
                $"build-release.ps1 ({version}) 실패. exit={p.ExitCode}\n{stdout.Result}\n{stderr.Result}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TextHotKey.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("TextHotKey.slnx를 찾지 못함");
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe))
            .FirstOrDefault(File.Exists);
}
