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


        public async Task<bool> CheckForUpdates()
        {
            var currVersion = this.GetCurrentVersion();
            //var latestVersion = await this.GetLatestVersion();
            var latestVersion = "2.0.0";

            if (currVersion == "" && latestVersion == "")
            {
                return false;
            }

            if (new Version(latestVersion) > new Version(currVersion)) {
                return true;
            } else
            {
                return false;
            }
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
