using System.Diagnostics;
using TextHotKey.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TextHotKey.Tests.E2E;

// Updater.exe를 직접 실행해 성공/실패 상황을 재현한다.
// 원칙: 어떤 실패에서도 (1) 설치 폴더가 구버전 그대로 남고 (2) 사용자에게 실행 가능한 앱이 남으며
//       (3) 실패 사유가 update-result.txt로 앱에 전달되어야 한다.
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public class UpdaterFailureTests(ReleaseBuildsFixture builds, ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Success_InstallMatchesNewPackage_AndRelaunches()
    {
        using var sandbox = NewInstall();
        var exit = RunUpdater(sandbox, builds.NewZip);

        Assert.Equal(0, exit);
        AssertInstallEquals(builds.NewAppDir, sandbox);
        Assert.False(Directory.Exists(Path.Combine(sandbox.InstallDir, ".update")), "작업 폴더(.update)가 남음");
        Assert.Equal("ok", ReadResult(sandbox)[0]);
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "앱이 재실행되지 않음");
    }

    [Fact]
    public void CorruptZip_KeepsInstallIntact_AndRelaunchesOldVersion()
    {
        using var sandbox = NewInstall();
        var before = InstallSandbox.HashTree(sandbox.InstallDir);
        var badZip = Path.Combine(sandbox.UpdateTempDir, "update.zip");
        var junk = new byte[1024 * 1024];
        Random.Shared.NextBytes(junk);
        File.WriteAllBytes(badZip, junk);

        var exit = RunUpdater(sandbox, badZip);

        Assert.NotEqual(0, exit);
        Assert.Equal(before, InstallSandbox.HashTree(sandbox.InstallDir));
        Assert.Equal(new[] { "fail", "업데이트 파일이 손상되었습니다. 이전 버전을 그대로 사용합니다." }, ReadResult(sandbox));
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "구버전 앱이 재실행되지 않음");
    }

    // 백신 검사·탐색기 미리보기 등으로 설치 폴더의 파일 하나가 잠겨 있는 경우.
    // 실패하더라도 구버전/신버전 파일이 섞인 설치본이 남으면 안 된다 → 구버전 그대로 복원.
    [Theory]
    [InlineData("TextHotKey.dll")]   // 앞쪽에서 이동되는 파일
    [InlineData("Updater.exe")]      // 거의 마지막에 이동되는 파일(앞의 파일들은 이미 옮겨진 뒤 되돌려야 함)
    [InlineData(@"ko\PresentationCore.resources.dll")] // 하위 폴더 파일
    public void LockedFile_RollsBackToOldVersion(string lockedFile)
    {
        using var sandbox = NewInstall();
        var lockedPath = Path.Combine(sandbox.InstallDir, lockedFile);
        Assert.True(File.Exists(lockedPath), $"테스트 전제: {lockedFile}이 패키지에 있어야 함");
        var before = InstallSandbox.HashTree(sandbox.InstallDir);

        int exit;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            exit = RunUpdater(sandbox, builds.NewZip);
        }

        Assert.Equal(99, exit);
        Assert.Equal(before, InstallSandbox.HashTree(sandbox.InstallDir));
        Assert.Equal("fail", ReadResult(sandbox)[0]);
        Assert.Contains("사용 중", ReadResult(sandbox)[1]);
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "실패 후 앱이 재실행되지 않음");
    }

    // 신버전 패키지에 없는 옛 파일은 지우되, 설치 마법사가 만든 제거 프로그램(unins*)은 남겨야 한다.
    [Fact]
    public void FilesRemovedInNewVersion_AreDeleted_ButUninstallerIsKept()
    {
        using var sandbox = NewInstall();
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "Obsolete.Library.dll"), "from old version");
        Directory.CreateDirectory(Path.Combine(sandbox.InstallDir, "old-lang"));
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "old-lang", "Old.resources.dll"), "from old version");
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "unins000.exe"), "uninstaller");
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "unins000.dat"), "uninstaller data");

        var exit = RunUpdater(sandbox, builds.NewZip);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(sandbox.InstallDir, "Obsolete.Library.dll")), "신버전 패키지에 없는 파일이 남아 있음");
        Assert.False(Directory.Exists(Path.Combine(sandbox.InstallDir, "old-lang")), "빈 옛 폴더가 남아 있음");
        Assert.Equal("uninstaller", File.ReadAllText(Path.Combine(sandbox.InstallDir, "unins000.exe")));
        Assert.Equal("uninstaller data", File.ReadAllText(Path.Combine(sandbox.InstallDir, "unins000.dat")));
        AssertInstallEquals(builds.NewAppDir, sandbox, ignore: f => f.StartsWith("unins", StringComparison.OrdinalIgnoreCase));
    }

    // 앱이 끝내 종료되지 않으면(응답 없음 등) 실행 중인 파일을 건드리지 않고 중단한다.
    [Fact]
    public void AppNotExiting_AbortsWithoutTouchingInstall()
    {
        using var sandbox = NewInstall();
        var before = InstallSandbox.HashTree(sandbox.InstallDir);
        using var app = sandbox.LaunchApp();
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout));

        var exit = RunUpdater(sandbox, builds.NewZip, "--pid", app.Id.ToString(), "--wait", "2000");

        Assert.Equal(3, exit);
        Assert.Equal(before, InstallSandbox.HashTree(sandbox.InstallDir));
        Assert.Equal("fail", ReadResult(sandbox)[0]);
        Assert.False(app.HasExited);
        Assert.Single(sandbox.RunningApps()); // 중복 실행하지 않음
    }

    // 지난번 업데이트가 비정상 종료돼 작업 폴더가 남아 있어도 정상적으로 진행한다.
    [Fact]
    public void LeftoverWorkDir_IsCleanedUp()
    {
        using var sandbox = NewInstall();
        var leftover = Path.Combine(sandbox.InstallDir, ".update", "staging");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "TextHotKey.dll"), "garbage");

        var exit = RunUpdater(sandbox, builds.NewZip);

        Assert.Equal(0, exit);
        AssertInstallEquals(builds.NewAppDir, sandbox);
    }

    [Fact]
    public void MissingZip_RelaunchesOldVersion()
    {
        using var sandbox = NewInstall();
        var exit = RunUpdater(sandbox, Path.Combine(sandbox.UpdateTempDir, "nope.zip"));

        Assert.NotEqual(0, exit);
        Assert.StartsWith(ReleaseBuildsFixture.OldVersion, sandbox.InstalledFileVersion());
        Assert.Equal("fail", ReadResult(sandbox)[0]);
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "앱이 재실행되지 않음");
    }

    private InstallSandbox NewInstall()
    {
        var sandbox = new InstallSandbox();
        sandbox.Install(builds.OldZip);
        return sandbox;
    }

    private static string[] ReadResult(InstallSandbox sandbox) =>
        File.ReadAllLines(Path.Combine(sandbox.UpdateTempDir, "update-result.txt"));

    // 설치 폴더가 기대 패키지와 정확히 같은지(빠진 파일도, 남은 파일도 없음).
    private static void AssertInstallEquals(string expectedDir, InstallSandbox sandbox, Func<string, bool>? ignore = null)
    {
        var expected = InstallSandbox.HashTree(expectedDir);
        var actual = InstallSandbox.HashTree(sandbox.InstallDir)
            .Where(kv => ignore == null || !ignore(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var wrong = expected.Where(kv => actual.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(wrong.Count == 0 && extra.Count == 0,
            $"설치 폴더가 패키지와 다름. 다르거나 없는 파일: [{string.Join(", ", wrong)}] 남은 파일: [{string.Join(", ", extra)}]");
    }

    // 앱의 StartUpdater와 같은 방식: 설치 폴더의 Updater.exe를 임시 폴더로 복사해 실행한다.
    // zip도 앱처럼 임시 폴더의 update.zip으로 넘긴다(Updater는 성공 시 zip을 지우므로 공용 산출물을 직접 넘기면 안 됨).
    private int RunUpdater(InstallSandbox sandbox, string zip, params string[] extraArgs)
    {
        var tempZip = Path.Combine(sandbox.UpdateTempDir, "update.zip");
        if (File.Exists(zip) && !string.Equals(Path.GetFullPath(zip), tempZip, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(zip, tempZip, overwrite: true);
            zip = tempZip;
        }

        var updater = Path.Combine(sandbox.UpdateTempDir, "Updater.exe");
        File.Copy(Path.Combine(sandbox.InstallDir, "Updater.exe"), updater, overwrite: true);

        var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = sandbox.UpdateTempDir };
        foreach (var a in new[] { "--zip", zip, "--dir", sandbox.InstallDir, "--exe", sandbox.ExePath, "--log", sandbox.UpdaterLogPath }.Concat(extraArgs))
            psi.ArgumentList.Add(a);
        sandbox.ApplyEnv(psi);

        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(90)), "Updater가 90초 내 끝나지 않음");
        output.WriteLine("---- updater.log ----\n" + sandbox.ReadUpdaterLog());
        return p.ExitCode;
    }
}
