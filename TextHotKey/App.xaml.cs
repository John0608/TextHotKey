using Microsoft.Win32;
using Octokit;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TextHotKey
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private const string RegKey = @"SOFTWARE\TextHotKey";
        private UpdateManager updateManager = new UpdateManager();

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterInstallPath();
        }

        private void RegisterInstallPath()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            var currentPath = Process.GetCurrentProcess().MainModule!.FileName;
            var installDir = Path.GetDirectoryName(currentPath)!;
            var currVersion = updateManager.GetCurrentVersion();

            key?.SetValue("InstallPath", currentPath);        // 실행 파일 경로
            key?.SetValue("InstallDir", installDir);           // 설치 폴더 경로
            key?.SetValue("Version", currVersion);          // 현재 버전
            key?.SetValue("UpdaterPath", Path.Combine(installDir, "Updater.exe")); // 업데이터 경로
        }

    }

}
