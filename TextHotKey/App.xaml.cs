using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace TextHotKey
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        // 세션 단위(Local\) 이름 — 같은 사용자 세션에서의 중복 실행을 막는다.
        private const string MutexName = "TextHotKey_SingleInstance_Mutex";
        private const string ShowEventName = "TextHotKey_SingleInstance_ShowEvent";
        private const string RegKey = @"SOFTWARE\TextHotKey";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private readonly UpdateManager _updateManager = new UpdateManager();

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // 이미 실행 중인 인스턴스가 있다 → 기존 창을 앞으로 가져오라고 신호하고 종료.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch { /* 신호 실패해도 조용히 종료 */ }

                Shutdown();
                return;
            }

            // 첫 인스턴스: 다른 인스턴스가 보내는 신호를 대기하는 백그라운드 리스너를 띄운다.
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showEvent = showEvent;
            var listener = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
            })
            {
                IsBackground = true,
                Name = "SingleInstanceListener"
            };
            listener.Start();

            base.OnStartup(e);

            // 설치 경로/버전을 레지스트리에 기록 (업데이터가 참조).
            RegisterInstallPath();

            // StartupUri 대신 첫 인스턴스에서만 메인 창을 직접 생성한다.
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        // 기존 메인 창을 복원하고 최상단으로 가져온다.
        private void ActivateMainWindow()
        {
            var window = MainWindow;
            if (window == null) return;

            // 트레이로 숨은(Hide) 창은 먼저 Show() 한 뒤 최소화를 풀어야 확실히 복원된다.
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            // 잠깐 Topmost를 켰다 꺼서 확실히 포그라운드로 끌어올린다.
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        // 설치 경로/폴더/버전/업데이터 경로를 레지스트리에 기록한다.
        private void RegisterInstallPath()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            var currentPath = Process.GetCurrentProcess().MainModule!.FileName;
            var installDir = Path.GetDirectoryName(currentPath)!;
            var currVersion = _updateManager.GetCurrentVersion();

            key?.SetValue("InstallPath", currentPath);        // 실행 파일 경로
            key?.SetValue("InstallDir", installDir);           // 설치 폴더 경로
            key?.SetValue("Version", currVersion);             // 현재 버전
            key?.SetValue("UpdaterPath", Path.Combine(installDir, "Updater.exe")); // 업데이터 경로
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showEvent?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
