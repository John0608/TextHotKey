using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using TextHotKey.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TextHotKey.Tests.Upgrade;

// 릴리스 직전 검증: "사용자가 지금 갖고 있는 이전 릴리스"에서 "이번에 발행할 패키지"로 실제 업데이트되는지 본다.
// (E2E 테스트는 현재 소스로 만든 두 버전 사이를 보므로, 이미 배포된 구버전의 앱/Updater로 올라오는 경로는 여기서만 확인된다.)
// release.yml이 scripts/verify-upgrade.ps1로 이전 릴리스 zip을 내려받아 아래 환경변수로 넘긴다:
//   THK_UPGRADE_FROM_ZIP / THK_UPGRADE_FROM_VERSION : 이전 릴리스 패키지와 태그(v 제외)
//   THK_UPGRADE_TO_ZIP   / THK_UPGRADE_TO_VERSION   : 이번 패키지와 표시 버전(예: 1.4.0, 1.4.0-beta.2)
[Trait("Category", "ReleaseUpgrade")]
public class ReleaseUpgradeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = Ui.Timeout;

    [ReleaseUpgradeFact]
    public void PreviousRelease_UpdatesToThisPackage()
    {
        var fromZip = Env("THK_UPGRADE_FROM_ZIP");
        var fromVersion = Env("THK_UPGRADE_FROM_VERSION");
        var toZip = Env("THK_UPGRADE_TO_ZIP");
        var toVersion = Env("THK_UPGRADE_TO_VERSION");

        using var sandbox = new InstallSandbox();
        using var server = new FakeReleaseServer { AssetFilePath = toZip };
        // 채널과 무관하게 이번 패키지가 "최신"으로 보이도록 정식 릴리스로 내놓는다.
        server.Releases.Add(new("v" + toVersion));
        sandbox.Install(fromZip);
        sandbox.WriteSettings("{\"AutoUpdate\":true}");

        var expectedDir = Path.Combine(sandbox.Root, "expected");
        ZipFile.ExtractToDirectory(toZip, expectedDir);

        // 테스트 격리 장치(THK_TEST_*)가 있는 버전이면 앱 UI부터 전 과정을, 없으면(≤1.4.0-beta.1)
        // 실제 PC를 건드리지 않도록 앱 대신 그 버전의 Updater.exe를 직접 실행해 교체 단계를 검증한다.
        var fullFlow = SupportsTestHooks(sandbox.InstallDir);
        output.WriteLine($"{fromVersion} -> {toVersion} ({(fullFlow ? "앱 UI 전 과정" : "구버전 Updater 교체 단계")})");

        try
        {
            if (fullFlow) RunThroughApp(sandbox, server);
            else RunLegacyUpdater(sandbox, server);

            Assert.True(InstallSandbox.WaitUntil(
                    () => sandbox.ReadAppLog().Contains($"App started. version={toVersion},"), Timeout),
                $"업데이트 후 {toVersion} 앱이 실행되지 않음");

            // 새 패키지의 모든 파일이 그대로 설치돼야 한다. 구 Updater는 옛 파일을 지우지 않으므로 남은 파일은 전 과정에서만 검사.
            var expected = InstallSandbox.HashTree(expectedDir);
            var actual = InstallSandbox.HashTree(sandbox.InstallDir);
            var wrong = expected.Where(kv => actual.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
            var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
            Assert.True(wrong.Count == 0, $"새 패키지와 다르거나 없는 파일: [{string.Join(", ", wrong)}]");
            if (fullFlow) Assert.True(extra.Count == 0, $"남은 옛 파일: [{string.Join(", ", extra)}]");
            else if (extra.Count > 0) output.WriteLine($"(참고) 구 Updater가 남긴 옛 파일: {string.Join(", ", extra)}");

            // 새 버전은 자신을 최신으로 판단하고(무한 업데이트 없음) 정상 실행 상태를 유지해야 한다.
            Assert.True(InstallSandbox.WaitUntil(
                    () => sandbox.ReadAppLog().Contains($"current:{toVersion}, latest:{toVersion}, available:False"), Timeout),
                "새 버전의 시작 시 업데이트 확인 결과가 '최신'이 아님");
            Thread.Sleep(2000);
            Assert.Single(sandbox.RunningApps());
            Assert.Equal(fullFlow ? 1 : 0, server.CountRequests("/download/"));
        }
        finally
        {
            output.WriteLine("---- app.log ----\n" + sandbox.ReadAppLog());
            output.WriteLine("---- updater.log ----\n" + sandbox.ReadUpdaterLog());
        }
    }

    // 구버전 앱 실행 → 업데이트 안내 '네' → 다운로드 → 앱 종료 → (구버전의) Updater가 교체.
    private static void RunThroughApp(InstallSandbox sandbox, FakeReleaseServer server)
    {
        using var oldApp = sandbox.LaunchApp(server.BaseUrl);
        Assert.True(Ui.ClickButton(oldApp, "네"), "이전 버전 앱에서 업데이트 안내('네' 버튼)가 뜨지 않음");
        Assert.True(oldApp.WaitForExit(Timeout), "다운로드 후 이전 버전 앱이 종료되지 않음");
    }

    // 앱이 하는 것처럼 설치 폴더의 Updater.exe를 임시 폴더로 복사해 다운로드된 zip으로 실행한다.
    // 구 Updater는 로그를 실제 %TEMP%\TextHotKey_update에 쓰므로 새로 추가된 부분만 출력한다.
    private void RunLegacyUpdater(InstallSandbox sandbox, FakeReleaseServer server)
    {
        var zip = Path.Combine(sandbox.UpdateTempDir, "update.zip");
        File.Copy(server.AssetFilePath!, zip, overwrite: true);
        var updater = Path.Combine(sandbox.UpdateTempDir, "Updater.exe");
        File.Copy(Path.Combine(sandbox.InstallDir, "Updater.exe"), updater, overwrite: true);

        var legacyLog = Path.Combine(Path.GetTempPath(), "TextHotKey_update", "updater.log");
        var logStart = File.Exists(legacyLog) ? new FileInfo(legacyLog).Length : 0;

        var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = sandbox.UpdateTempDir };
        foreach (var a in new[] { "--zip", zip, "--dir", sandbox.InstallDir, "--exe", sandbox.ExePath, "--log", sandbox.UpdaterLogPath })
            psi.ArgumentList.Add(a);
        sandbox.ApplyEnv(psi, server.BaseUrl); // 재실행되는 새 버전 앱이 이 환경(격리 폴더·가짜 서버)을 물려받는다.

        using var p = Process.Start(psi)!;
        var exited = p.WaitForExit(TimeSpan.FromSeconds(120));
        if (File.Exists(legacyLog))
        {
            using var fs = new FileStream(legacyLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Min(logStart, fs.Length), SeekOrigin.Begin);
            output.WriteLine("---- legacy updater.log ----\n" + new StreamReader(fs).ReadToEnd());
        }
        Assert.True(exited, "이전 버전 Updater가 120초 내 끝나지 않음");
        Assert.Equal(0, p.ExitCode);
    }

    // 앱 어셈블리에 테스트 격리 환경변수 이름이 들어 있는지로 판단한다(문자열 리터럴은 UTF-16으로 저장됨).
    private static bool SupportsTestHooks(string installDir)
    {
        var dll = Path.Combine(installDir, "TextHotKey.dll");
        if (!File.Exists(dll)) return false;
        var bytes = File.ReadAllBytes(dll);
        var needle = Encoding.Unicode.GetBytes("THK_TEST_UPDATE_API");
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)!;
}

// 검증 대상(환경변수)이 주어졌을 때만 실행한다. 일반 테스트 실행에서는 건너뛴다.
public sealed class ReleaseUpgradeFactAttribute : FactAttribute
{
    private static readonly string[] Required =
        ["THK_UPGRADE_FROM_ZIP", "THK_UPGRADE_FROM_VERSION", "THK_UPGRADE_TO_ZIP", "THK_UPGRADE_TO_VERSION"];

    public ReleaseUpgradeFactAttribute()
    {
        var missing = Required.Where(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))).ToList();
        if (missing.Count > 0)
            Skip = "릴리스 업데이트 검증 전용(scripts/verify-upgrade.ps1에서 실행). 누락: " + string.Join(", ", missing);
    }
}
