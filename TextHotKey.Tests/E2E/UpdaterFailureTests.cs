using System.Diagnostics;
using TextHotKey.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TextHotKey.Tests.E2E;

// Updater.exe를 직접 실행해 실패 상황을 재현한다.
// 원칙: 어떤 실패에서도 (1) 설치 폴더가 한 버전으로 일관되고 (2) 사용자에게 실행 가능한 앱이 남아야 한다.
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public class UpdaterFailureTests(ReleaseBuildsFixture builds, ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Success_ReplacesFiles_AndRelaunches()
    {
        using var sandbox = NewInstall();
        var exit = RunUpdater(sandbox, builds.NewZip);

        Assert.Equal(0, exit);
        Assert.StartsWith(ReleaseBuildsFixture.NewVersion, sandbox.InstalledFileVersion());
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
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "구버전 앱이 재실행되지 않음");
    }

    // 백신 검사·탐색기 미리보기·종료가 늦은 앱 등으로 설치 폴더의 파일 하나가 잠겨 있는 경우.
    // 실패하더라도 구버전/신버전 파일이 섞인 설치본이 남으면 안 된다.
    [Fact(Skip = KnownIssues.PartialUpdate)]
    public void LockedFile_LeavesSingleConsistentVersion()
    {
        using var sandbox = NewInstall();
        int exit;
        using (new FileStream(Path.Combine(sandbox.InstallDir, "TextHotKey.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            exit = RunUpdater(sandbox, builds.NewZip);
        }

        var oldTree = InstallSandbox.HashTree(builds.OldAppDir);
        var newTree = InstallSandbox.HashTree(builds.NewAppDir);
        var installed = InstallSandbox.HashTree(sandbox.InstallDir);
        var changed = oldTree.Keys.Where(k => newTree.TryGetValue(k, out var h) && h != oldTree[k]).ToList();
        var fromOld = changed.Where(k => installed.GetValueOrDefault(k) == oldTree[k]).ToList();
        var fromNew = changed.Where(k => installed.GetValueOrDefault(k) == newTree[k]).ToList();
        output.WriteLine($"exit={exit}, 버전별로 다른 파일={string.Join(", ", changed)}");
        output.WriteLine($"구버전으로 남은 파일={string.Join(", ", fromOld)}");
        output.WriteLine($"신버전으로 바뀐 파일={string.Join(", ", fromNew)}");

        Assert.True(fromOld.Count == changed.Count || fromNew.Count == changed.Count,
            $"구/신버전 파일이 섞인 설치본이 남음. 구버전: [{string.Join(", ", fromOld)}] 신버전: [{string.Join(", ", fromNew)}]");
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "실패 후 앱이 재실행되지 않음");
    }

    // 신버전에서 빠진 파일이 설치 폴더에 계속 남으면, 오래된 DLL이 누적되고 로드 충돌의 원인이 될 수 있다.
    [Fact(Skip = KnownIssues.StaleFiles)]
    public void FilesRemovedInNewVersion_AreDeleted()
    {
        using var sandbox = NewInstall();
        var stale = Path.Combine(sandbox.InstallDir, "Obsolete.Library.dll");
        File.WriteAllText(stale, "from old version");

        var exit = RunUpdater(sandbox, builds.NewZip);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(stale), "신버전 패키지에 없는 파일이 남아 있음");
    }

    [Fact]
    public void MissingZip_RelaunchesOldVersion()
    {
        using var sandbox = NewInstall();
        var exit = RunUpdater(sandbox, Path.Combine(sandbox.UpdateTempDir, "nope.zip"));

        Assert.NotEqual(0, exit);
        Assert.StartsWith(ReleaseBuildsFixture.OldVersion, sandbox.InstalledFileVersion());
        Assert.True(InstallSandbox.WaitUntil(() => sandbox.RunningApps().Count == 1, Timeout), "앱이 재실행되지 않음");
    }

    private InstallSandbox NewInstall()
    {
        var sandbox = new InstallSandbox();
        sandbox.Install(builds.OldZip);
        return sandbox;
    }

    // 앱의 StartUpdater와 같은 방식: 설치 폴더의 Updater.exe를 임시 폴더로 복사해 실행한다.
    // zip도 앱처럼 임시 폴더의 update.zip으로 넘긴다(Updater는 성공 시 zip을 지우므로 공용 산출물을 직접 넘기면 안 됨).
    private int RunUpdater(InstallSandbox sandbox, string zip)
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
        foreach (var a in new[] { "--zip", zip, "--dir", sandbox.InstallDir, "--exe", sandbox.ExePath, "--log", sandbox.UpdaterLogPath })
            psi.ArgumentList.Add(a);
        sandbox.ApplyEnv(psi);

        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(90)), "Updater가 90초 내 끝나지 않음");
        output.WriteLine("---- updater.log ----\n" + sandbox.ReadUpdaterLog());
        return p.ExitCode;
    }
}
