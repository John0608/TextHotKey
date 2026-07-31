using MaterialDesignThemes.Wpf;
using Octokit;
using System.Diagnostics;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace TextHotKey
{
    internal class UpdateManager
    {
        private const string GitHubOwner = "John0608";
        private const string GitHubRepo = "TextHotKey";


        // 현재 버전과 GitHub 최신 릴리스를 비교한다.
        // Failed=true는 조회 실패(네트워크 등), UpdateAvailable=true는 새 버전 있음.
        public async Task<(bool UpdateAvailable, string Current, string Latest, bool Failed)> CheckAsync()
        {
            var current = GetCurrentVersion();
            var latest = await GetLatestVersion();

            if (string.IsNullOrEmpty(latest))
                return (false, current, "", true);

            bool available = Version.TryParse(latest, out var lv)
                          && Version.TryParse(current, out var cv)
                          && lv > cv;
            return (available, current, latest, false);
        }

        // 최신 릴리스 페이지를 기본 브라우저로 연다.
        public void OpenReleasesPage()
        {
            var url = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }

        public async Task<string> GetLatestVersion()
        {
            try
            {
                var client = new GitHubClient(new ProductHeaderValue("TextHotKey"));
                var latest = await client.Repository.Release.GetLatest(GitHubOwner, GitHubRepo);
                var latestVersion = latest.TagName.TrimStart("v").ToString();
                Logger.Info($"Latest version: {latestVersion}");

                return latestVersion;
            } catch
            {
                Logger.Error("version is null");
                return "";
            }
        }

        public string GetCurrentVersion()
        {
            var Version = Assembly.GetExecutingAssembly().GetName().Version!;
            var versionStr = Version != null ? $"{Version.Major}.{Version.Minor}.{Version.Build}" : "";
            Logger.Info($"Current version: {versionStr}");

            return versionStr;
        }

        public async Task<bool> StartUpdater()
        {
            // Updater.exe 실행 (다운로드된 파일 경로 전달)
            var updaterPath = Path.Combine(
                Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!,
                "Updater.exe");

            if (!File.Exists(updaterPath)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = true
            });

            return true;
        }
    }


}
