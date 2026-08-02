using Octokit;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;

namespace TextHotKey
{
    // 업데이트 확인 결과.
    //  Failed=true  : 조회 자체 실패(네트워크/인증 등). Latest/DownloadUrl 무의미.
    //  Failed=false : 정상 조회. UpdateAvailable/DownloadUrl 참조.
    internal record UpdateInfo(
        bool UpdateAvailable,
        string Current,
        string Latest,
        string? DownloadUrl,
        bool Failed);

    internal class UpdateManager
    {
        private const string GitHubOwner = "John0608";
        private const string GitHubRepo = "TextHotKey";

        // 릴리스에 첨부하는 자체 포함 패키지 이름 규칙(build-release.ps1 / release.yml과 일치).
        private const string AssetName = "TextHotKey-win-x64.zip";

        // 현재 버전과 GitHub 최신 릴리스를 비교한다.
        public async Task<UpdateInfo> CheckAsync()
        {
            var current = GetCurrentVersion();

            Release latest;
            try
            {
                var client = new GitHubClient(new ProductHeaderValue("TextHotKey"));
                latest = await client.Repository.Release.GetLatest(GitHubOwner, GitHubRepo);
            }
            catch (NotFoundException)
            {
                // 아직 게시된 릴리스가 없음 → 최신 상태로 간주(조회 실패 아님).
                Logger.Info("No published release found.");
                return new UpdateInfo(false, current, "", null, false);
            }
            catch (Exception ex)
            {
                // 네트워크/인증 등 실제 조회 실패.
                Logger.Error($"Update check failed: {ex.Message}");
                return new UpdateInfo(false, current, "", null, true);
            }

            var latestVersion = latest.TagName.TrimStart('v');
            Logger.Info($"Latest version: {latestVersion}");

            bool available = Version.TryParse(latestVersion, out var lv)
                          && Version.TryParse(current, out var cv)
                          && lv > cv;

            // 자동 설치용 zip 에셋을 찾는다. 규칙 이름 우선, 없으면 첫 zip.
            var asset = latest.Assets.FirstOrDefault(a =>
                            a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                        ?? latest.Assets.FirstOrDefault(a =>
                            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo(available, current, latestVersion, asset?.BrowserDownloadUrl, false);
        }

        // 최신 릴리스 페이지를 기본 브라우저로 연다.
        public void OpenReleasesPage()
        {
            var url = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }

        public string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "";
            Logger.Info($"Current version: {versionStr}");

            return versionStr;
        }

        // 지정 URL의 파일을 destPath로 내려받는다. 진행률(0~1)을 progress로 보고한다.
        public async Task DownloadAsync(
            string url,
            string destPath,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TextHotKey");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                    progress?.Report((double)read / total.Value);
            }
        }

        // Updater.exe를 임시 폴더로 복사해 실행하고, 다운로드된 zip 경로·설치 대상·현재 PID를 넘긴다.
        // 설치 폴더의 Updater.exe까지 덮어쓸 수 있도록 반드시 임시 복사본에서 실행한다.
        public bool StartUpdater(string zipPath)
        {
            var exePath = Process.GetCurrentProcess().MainModule!.FileName;
            var installDir = Path.GetDirectoryName(exePath)!;
            var srcUpdater = Path.Combine(installDir, "Updater.exe");

            if (!File.Exists(srcUpdater))
            {
                Logger.Error($"Updater.exe not found: {srcUpdater}");
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "TextHotKey_update");
            Directory.CreateDirectory(tempDir);

            var tempUpdater = Path.Combine(tempDir, "Updater.exe");
            File.Copy(srcUpdater, tempUpdater, overwrite: true);

            var pid = Environment.ProcessId;
            var psi = new ProcessStartInfo
            {
                FileName = tempUpdater,
                UseShellExecute = false,
                WorkingDirectory = tempDir,
            };
            psi.ArgumentList.Add("--zip"); psi.ArgumentList.Add(zipPath);
            psi.ArgumentList.Add("--dir"); psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add("--exe"); psi.ArgumentList.Add(exePath);
            psi.ArgumentList.Add("--pid"); psi.ArgumentList.Add(pid.ToString());

            Logger.Info($"Starting updater: dir={installDir}, zip={zipPath}, pid={pid}");
            Process.Start(psi);
            return true;
        }
    }
}
