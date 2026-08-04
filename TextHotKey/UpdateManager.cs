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

        // 현재 버전과 GitHub 릴리스를 비교한다.
        // includePrerelease=true 면 사전 릴리스(베타)까지 포함해 가장 높은 버전을 고른다.
        public async Task<UpdateInfo> CheckAsync(bool includePrerelease = false)
        {
            var currentVer = GetCurrentVersionObject();
            var currentStr = FormatVersion(currentVer);

            Release? chosen;
            try
            {
                var client = new GitHubClient(new ProductHeaderValue("TextHotKey"));
                if (includePrerelease)
                {
                    // 사전 릴리스 포함 전체에서 태그를 버전으로 파싱해 가장 높은 것을 고른다.
                    var all = await client.Repository.Release.GetAll(GitHubOwner, GitHubRepo);
                    chosen = all
                        .Where(r => !r.Draft)
                        .Select(r => new { Release = r, Ver = ParseTag(r.TagName) })
                        .Where(x => x.Ver != null)
                        .OrderByDescending(x => x.Ver)
                        .Select(x => x.Release)
                        .FirstOrDefault();
                }
                else
                {
                    // 안정판만: GetLatest는 사전 릴리스/드래프트를 자동 제외한다.
                    chosen = await client.Repository.Release.GetLatest(GitHubOwner, GitHubRepo);
                }
            }
            catch (NotFoundException)
            {
                // 아직 게시된 릴리스가 없음 → 최신 상태로 간주(조회 실패 아님).
                Logger.Info("No published release found.");
                return new UpdateInfo(false, currentStr, "", null, false);
            }
            catch (Exception ex)
            {
                // 네트워크/인증 등 실제 조회 실패.
                Logger.Error($"Update check failed: {ex.Message}");
                return new UpdateInfo(false, currentStr, "", null, true);
            }

            if (chosen == null)
                return new UpdateInfo(false, currentStr, "", null, false);

            var latestVer = ParseTag(chosen.TagName);
            var latestStr = chosen.TagName.TrimStart('v', 'V');
            Logger.Info($"Latest version: {latestStr} (prerelease={chosen.Prerelease})");

            bool available = latestVer != null && latestVer > currentVer;

            // 자동 설치용 zip 에셋을 찾는다. 규칙 이름 우선, 없으면 첫 zip.
            var asset = chosen.Assets.FirstOrDefault(a =>
                            a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                        ?? chosen.Assets.FirstOrDefault(a =>
                            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo(available, currentStr, latestStr, asset?.BrowserDownloadUrl, false);
        }

        // 태그(vX.Y.Z 또는 vX.Y.Z-beta.N)를 4-part Version으로 변환한다.
        //  vX.Y.Z        -> X.Y.Z.0
        //  vX.Y.Z-beta.N -> X.Y.Z.N  (같은 X.Y.Z 안정판(.0)보다 높게 정렬됨)
        private static Version? ParseTag(string tag)
        {
            var s = tag.TrimStart('v', 'V');
            var m = System.Text.RegularExpressions.Regex.Match(
                s, @"^(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?$");
            if (!m.Success) return null;
            int beta = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            return new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
                beta);
        }

        // 어셈블리 버전을 항상 4-part로 정규화(3-part 안정판은 Revision=0으로).
        private static Version GetCurrentVersionObject()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        private static string FormatVersion(Version v)
            => v.Revision > 0
                ? $"{v.Major}.{v.Minor}.{v.Build}-beta.{v.Revision}"
                : $"{v.Major}.{v.Minor}.{v.Build}";

        // 최신 릴리스 페이지를 기본 브라우저로 연다.
        public void OpenReleasesPage()
        {
            var url = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }

        // 레지스트리 기록 등에 쓰는 현재 버전 문자열.
        public string GetCurrentVersion()
        {
            var versionStr = FormatVersion(GetCurrentVersionObject());
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
