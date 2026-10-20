using System.Diagnostics;
using TextHotKey.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TextHotKey.Tests.E2E;

// 실제 릴리스 산출물로 "구버전 설치 → 앱이 업데이트 감지 → 사용자가 '네' → 다운로드 → 종료 →
// Updater가 교체 → 신버전 재실행" 전 과정을 실행한다. 사용자 클릭은 UI 자동화(FlaUI)로 한다.
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public class UpdateE2ETests(ReleaseBuildsFixture builds, ITestOutputHelper output)
{
    private static readonly TimeSpan UiTimeout = Ui.Timeout;

    [Fact]
    public void AutoUpdate_ReplacesInstall_AndRelaunchesNewVersion()
    {
        using var sandbox = new InstallSandbox();
        using var server = new FakeReleaseServer { AssetFilePath = builds.NewZip };
        server.Releases.Add(new("v" + ReleaseBuildsFixture.NewVersion));
        sandbox.Install(builds.OldZip);
        sandbox.WriteSettings("{\"AutoUpdate\":true}");
        Assert.StartsWith(ReleaseBuildsFixture.OldVersion, sandbox.InstalledFileVersion());

        try
        {
            using var oldApp = sandbox.LaunchApp(server.BaseUrl);

            // 1) 시작 시 업데이트 안내 → '네' 클릭
            Assert.True(ClickButton(oldApp, "네"), "업데이트 안내 다이얼로그('네' 버튼)가 뜨지 않음");

            // 2) 다운로드 완료 후 구버전 앱이 스스로 종료되어야 한다(100% 멈춤 버그 회귀 방지)
            Assert.True(oldApp.WaitForExit(UiTimeout), "다운로드 후 구버전 앱이 종료되지 않음(다이얼로그에서 멈춤?)");
            Assert.Equal(1, server.CountRequests("/download/"));

            // 3) Updater가 파일을 교체하고 신버전을 재실행
            Assert.True(InstallSandbox.WaitUntil(
                    () => sandbox.InstalledFileVersion()!.StartsWith(ReleaseBuildsFixture.NewVersion), UiTimeout),
                $"설치 폴더 exe 버전이 {ReleaseBuildsFixture.NewVersion}로 바뀌지 않음 (현재 {sandbox.InstalledFileVersion()})");
            Assert.True(InstallSandbox.WaitUntil(() => sandbox.ReadUpdaterLog().Contains("업데이트 성공"), UiTimeout),
                "updater.log에 '업데이트 성공'이 없음");
            Assert.True(InstallSandbox.WaitUntil(
                    () => sandbox.ReadAppLog().Contains($"App started. version={ReleaseBuildsFixture.NewVersion}"), UiTimeout),
                "신버전 앱이 재실행되지 않음");

            // 4) 설치 폴더 내용이 신버전 패키지와 정확히 같아야 한다
            AssertTreeEqual(builds.NewAppDir, sandbox.InstallDir);

            // 5) 재실행된 신버전은 다시 업데이트를 시도하지 않고(무한 업데이트 방지) 계속 살아 있어야 한다
            Assert.True(InstallSandbox.WaitUntil(
                () => sandbox.ReadAppLog().Contains($"current:{ReleaseBuildsFixture.NewVersion}, latest:{ReleaseBuildsFixture.NewVersion}, available:False"),
                UiTimeout), "신버전의 시작 시 업데이트 확인 로그가 없음");
            Thread.Sleep(2000);
            Assert.Single(sandbox.RunningApps());
            Assert.Equal(1, server.CountRequests("/download/"));
        }
        finally
        {
            Dump(sandbox);
        }
    }

    [Fact]
    public void DownloadFailure_ShowsError_AndKeepsOldVersionRunning()
    {
        using var sandbox = new InstallSandbox();
        using var server = new FakeReleaseServer { Mode = FakeReleaseServer.DownloadMode.NotFound };
        server.Releases.Add(new("v" + ReleaseBuildsFixture.NewVersion));
        sandbox.Install(builds.OldZip);
        sandbox.WriteSettings("{\"AutoUpdate\":true}");
        var before = InstallSandbox.HashTree(sandbox.InstallDir);

        try
        {
            using var app = sandbox.LaunchApp(server.BaseUrl);
            Assert.True(ClickButton(app, "네"), "업데이트 안내 다이얼로그가 뜨지 않음");
            Assert.True(WaitForText(app, "업데이트 중 오류"), "다운로드 실패 안내가 표시되지 않음");

            Assert.False(app.HasExited, "다운로드 실패 후 앱이 종료됨");
            Assert.False(File.Exists(sandbox.UpdaterLogPath), "다운로드 실패인데 Updater가 실행됨");
            Assert.Equal(before, InstallSandbox.HashTree(sandbox.InstallDir));
        }
        finally
        {
            Dump(sandbox);
        }
    }

    // 받은 패키지가 손상된 경우: Updater가 실패 → 구버전 그대로 재실행 → 앱이 실패 사유를 안내하고,
    // 같은 실행에서 곧바로 다시 업데이트를 시도하지 않는다.
    [Fact]
    public void CorruptPackage_RestoresOldVersion_AndAppShowsFailure()
    {
        using var sandbox = new InstallSandbox();
        var junk = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(junk);
        using var server = new FakeReleaseServer { AssetBytes = junk };
        server.Releases.Add(new("v" + ReleaseBuildsFixture.NewVersion));
        sandbox.Install(builds.OldZip);
        sandbox.WriteSettings("{\"AutoUpdate\":true}");
        var before = InstallSandbox.HashTree(sandbox.InstallDir);

        try
        {
            using var oldApp = sandbox.LaunchApp(server.BaseUrl);
            Assert.True(ClickButton(oldApp, "네"), "업데이트 안내 다이얼로그가 뜨지 않음");
            Assert.True(oldApp.WaitForExit(UiTimeout), "다운로드 후 앱이 종료되지 않음");

            Process? relaunched = null;
            Assert.True(InstallSandbox.WaitUntil(() => (relaunched = sandbox.RunningApps().FirstOrDefault()) != null, UiTimeout),
                "실패 후 구버전 앱이 재실행되지 않음");
            Assert.True(WaitForText(relaunched!, "업데이트에 실패해 이전 버전을 계속 사용합니다"), "업데이트 실패 안내가 표시되지 않음");
            Assert.True(WaitForText(relaunched!, "업데이트 파일이 손상되었습니다"), "실패 사유가 표시되지 않음");

            Assert.Equal(before, InstallSandbox.HashTree(sandbox.InstallDir));
            Assert.Equal(1, server.CountRequests("/download/")); // 재실행 직후 다시 받지 않음
            Assert.False(File.Exists(Path.Combine(sandbox.UpdateTempDir, "update-result.txt")), "결과 파일이 소비되지 않음");
        }
        finally
        {
            Dump(sandbox);
        }
    }

    private static bool ClickButton(Process process, string name) => Ui.ClickButton(process, name);

    private static bool WaitForText(Process process, string text) => Ui.WaitForText(process, text);

    private static void AssertTreeEqual(string expectedDir, string actualDir)
    {
        var expected = InstallSandbox.HashTree(expectedDir);
        var actual = InstallSandbox.HashTree(actualDir);
        var mismatched = expected.Where(kv => !actual.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(mismatched.Count == 0 && extra.Count == 0,
            $"신버전과 다르거나 없는 파일: [{string.Join(", ", mismatched)}] 남은 파일: [{string.Join(", ", extra)}]");
    }

    private void Dump(InstallSandbox sandbox)
    {
        output.WriteLine("---- app.log ----\n" + sandbox.ReadAppLog());
        output.WriteLine("---- updater.log ----\n" + sandbox.ReadUpdaterLog());
    }
}
