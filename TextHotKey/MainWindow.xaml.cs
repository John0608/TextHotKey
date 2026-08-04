using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TextHotKey
{
    public partial class MainWindow : FluentWindow
    {
        private bool _isActive = false;
        private readonly HotkeyManager _hotkeyManager = new HotkeyManager();
        private readonly SettingManager settingManager = new SettingManager();
        private readonly UpdateManager updateManager = new UpdateManager();
        private readonly BetaManager betaManager;
        private bool _betaApproved;

        // 시스템 트레이(알림 영역) 아이콘. 닫기·최소화 시 여기로 숨는다.
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _exitRequested;

        // 목업과 맞춘 강조색(파랑). 시스템 강조색 대신 이 색을 테마별로 적용한다.
        private static readonly System.Windows.Media.Color AccentSeed =
            System.Windows.Media.Color.FromRgb(0x0F, 0x6C, 0xBD);

        // 코드에서 테마 리소스로 칠하는 요소들의 현재 상태(테마 전환 시 다시 칠하기 위함).
        private bool _hotkeysViewActive = true;
        private string _betaStatusTextValue = "확인 전";
        private bool? _betaStatusApprovedValue;

        public MainWindow()
        {
            InitializeComponent();
            betaManager = new BetaManager(settingManager);

            // 저장된 테마 + 커스텀 강조색 적용.
            ApplyTheme(settingManager.GetTheme());

            HotkeyItems.ItemsSource = _hotkeyManager.HotkeyList;
            UpdateHotkeyCount();
            ShowView(hotkeys: true); // 시작 탭(단축키) 강조 초기화

            AutoStartToggle.IsChecked = IsAutoStartEnabled();
            AutoUpdateToggle.IsChecked = settingManager.GetAutoUpdate();
            UpdateStatusText.Text = $"현재 v{updateManager.GetCurrentVersion()}";

            // 테스트(베타) 프로그램 UI 초기화.
            BetaCodeText.Text = betaManager.GetDeviceCode();
            BetaEmailBox.Text = betaManager.GetEmail();
            BetaUpdateToggle.IsChecked = settingManager.GetBetaOptIn();

            SetupTrayIcon();
            Closing += Window_Closing;
            StateChanged += Window_StateChanged;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 베타 승인 상태를 조용히 갱신(백그라운드).
            _ = RefreshBetaStatusAsync();

            if (!settingManager.GetAutoUpdate()) return;

            var info = await updateManager.CheckAsync(await ShouldUseBetaChannelAsync());
            Logger.Info($"Startup update check - current:{info.Current}, latest:{info.Latest}, available:{info.UpdateAvailable}, failed:{info.Failed}");
            UpdateSettingsUpdateStatus(info);

            // 시작 시엔 실패/최신이면 조용히 넘어가고, 새 버전이 있을 때만 안내한다.
            if (info.Failed || !info.UpdateAvailable) return;

            await PromptAndInstallAsync(info);
        }

        // ==================== 시스템 트레이 ====================

        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "TextHotKey",
                Visible = true,
                Icon = LoadTrayIcon(),
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("열기", null, (_, _) => RestoreFromTray());
            menu.Items.Add("종료", null, (_, _) => ExitApp());
            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var stream = System.Windows.Application
                    .GetResourceStream(new Uri("pack://application:,,,/favicon.ico"))?.Stream;
                if (stream != null) return new System.Drawing.Icon(stream);
            }
            catch { /* 아래 폴백 */ }

            try
            {
                if (Environment.ProcessPath != null)
                    return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)!;
            }
            catch { /* 아래 폴백 */ }

            return System.Drawing.SystemIcons.Application;
        }

        // 트레이에서 창 복원.
        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // 실제 종료(트레이 메뉴 '종료' 또는 업데이트 재실행 시).
        private void ExitApp()
        {
            _exitRequested = true;
            _hotkeyManager.UnregisterAll(new WindowInteropHelper(this).Handle);
            _trayIcon?.Dispose();
            _trayIcon = null;
            System.Windows.Application.Current.Shutdown();
        }

        // 타이틀바 최소화 버튼 → 최소화(= StateChanged에서 트레이로 숨김).
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // 타이틀바 닫기 버튼 → Close(= Window_Closing에서 취소하고 트레이로 숨김).
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // 닫기 → 종료하지 않고 트레이로 숨긴다.
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_exitRequested) return;
            e.Cancel = true;
            Hide();
        }

        // 최소화 → 작업표시줄 대신 트레이로 숨긴다.
        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        }

        // ==================== 테스트(베타) 프로그램 ====================

        // 베타 채널을 쓸지: 옵트인 켜짐 + 승인됨 둘 다여야 한다.
        private async Task<bool> ShouldUseBetaChannelAsync()
        {
            if (!settingManager.GetBetaOptIn()) return false;
            return await betaManager.IsApprovedAsync();
        }

        // 허용목록을 조회해 승인 상태 표시/토글 활성화를 갱신한다.
        private async Task RefreshBetaStatusAsync()
        {
            SetBetaStatus("확인 중…", approved: null);
            bool approved = await betaManager.IsApprovedAsync();
            _betaApproved = approved;

            SetBetaStatus(approved ? "승인됨 ✓" : "미승인", approved);
            BetaUpdateToggle.IsEnabled = approved;
        }

        // 베타 상태 pill 텍스트/색을 갱신한다. approved=null 이면 확인 중(중립).
        private void SetBetaStatus(string text, bool? approved)
        {
            _betaStatusTextValue = text;
            _betaStatusApprovedValue = approved;
            BetaStatusText.Text = text;
            string bg = approved == true ? "SystemFillColorSuccessBackgroundBrush" : "SubtleFillColorSecondaryBrush";
            string fg = approved == true ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush";
            BetaStatusPill.Background = (System.Windows.Media.Brush)FindResource(bg);
            BetaStatusText.Foreground = (System.Windows.Media.Brush)FindResource(fg);
        }

        // 테스트 신청: 이메일 저장 후 코드·이메일이 담긴 GitHub 이슈를 연다.
        private void BetaRequestButton_Click(object sender, RoutedEventArgs e)
        {
            var email = BetaEmailBox.Text.Trim();
            betaManager.SetEmail(email);
            var url = betaManager.BuildRequestIssueUrl(email);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        private async void BetaRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshBetaStatusAsync();
        }

        private void BetaUpdateToggle_Checked(object sender, RoutedEventArgs e)
        {
            settingManager.SetBetaOptIn(true);
        }

        private void BetaUpdateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            settingManager.SetBetaOptIn(false);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
        }

        // 단축키 메시지 처리
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && _isActive)
            {
                int id = wParam.ToInt32();
                if (id >= 0 && id < _hotkeyManager.HotkeyList.Count)
                {
                    handled = true;
                    SendText(_hotkeyManager.HotkeyList[id].Text);
                }
            }
            return IntPtr.Zero;
        }

        // 텍스트 입력

        // 이 길이 이상이면 클립보드 붙여넣기, 미만이면 키 입력으로 처리한다.
        private const int ClipboardPasteThreshold = 20;

        // 설정된 텍스트를 활성 창에 입력한다.
        // - 짧은 텍스트: 유니코드 키 입력 (붙여넣기를 지원하지 않는 곳에서도 동작)
        // - 긴 텍스트: 클립보드에 넣고 Ctrl+V로 한 번에 붙여넣기 (길이와 무관하게 즉시 삽입)
        private void SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (text.Length >= ClipboardPasteThreshold)
                SendViaClipboard(text);
            else
                SendViaKeystrokes(text);
        }

        // 전체 문자열을 단 한 번의 SendInput 호출로 입력한다.
        // (문자마다 SendInput을 호출하던 기존 방식보다 훨씬 빠르고 글자 누락이 없다.)
        private void SendViaKeystrokes(string text)
        {
            var inputs = new List<INPUT>(text.Length * 2 + 3);

            // 단축키 조합으로 눌려 있는 Ctrl/Alt/Shift를 먼저 떼어
            // 텍스트가 조합키와 함께 입력되는 것을 방지한다. (기존 100ms 지연 대체)
            AppendModifierRelease(inputs, VK_CONTROL);
            AppendModifierRelease(inputs, VK_MENU);
            AppendModifierRelease(inputs, VK_SHIFT);

            // 각 문자를 유니코드 입력(KEYEVENTF_UNICODE)으로 변환해 배치에 담는다.
            // 유니코드 입력은 키보드 레이아웃/조합키의 영향을 받지 않는다.
            foreach (char c in text)
            {
                inputs.Add(MakeUnicodeInput(c, keyUp: false));
                inputs.Add(MakeUnicodeInput(c, keyUp: true));
            }

            var array = inputs.ToArray();
            SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        }

        // 텍스트를 클립보드에 넣고 Ctrl+V로 붙여넣는다. 길이와 무관하게 즉시 삽입된다.
        private void SendViaClipboard(string text)
        {
            // 기존 클립보드 텍스트를 백업했다가 붙여넣기 후 복원한다.
            string? backup = null;
            try { if (System.Windows.Clipboard.ContainsText()) backup = System.Windows.Clipboard.GetText(); }
            catch { }

            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch
            {
                // 클립보드 사용 실패 시 키 입력 방식으로 대체한다.
                SendViaKeystrokes(text);
                return;
            }

            SendPaste();

            // 대상 앱이 붙여넣기를 처리할 시간을 준 뒤 원래 클립보드를 복원한다.
            if (backup != null)
            {
                var restore = backup;
                Task.Delay(300).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { try { System.Windows.Clipboard.SetText(restore); } catch { } }));
            }
        }

        // 눌려 있는 조합키를 모두 떼고 깨끗한 Ctrl+V를 전송한다.
        private void SendPaste()
        {
            var inputs = new List<INPUT>(7);

            AppendModifierRelease(inputs, VK_CONTROL);
            AppendModifierRelease(inputs, VK_MENU);
            AppendModifierRelease(inputs, VK_SHIFT);

            inputs.Add(MakeVkInput(VK_CONTROL, keyUp: false));
            inputs.Add(MakeVkInput(VK_V, keyUp: false));
            inputs.Add(MakeVkInput(VK_V, keyUp: true));
            inputs.Add(MakeVkInput(VK_CONTROL, keyUp: true));

            var array = inputs.ToArray();
            SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        }

        // 해당 수정 키가 눌려 있으면 keyup 이벤트를 배치에 추가한다.
        private static void AppendModifierRelease(List<INPUT> inputs, int vk)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) return;
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        // 문자 하나를 유니코드 keydown/keyup INPUT 구조체로 만든다.
        private static INPUT MakeUnicodeInput(char c, bool keyUp)
        {
            uint flags = KEYEVENTF_UNICODE;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        // 가상 키 하나를 keydown 또는 keyup INPUT 구조체로 만든다.
        private static INPUT MakeVkInput(int vk, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        // SendInput 기반 고속 텍스트 입력 인터롭
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        // ==================== 테마 ====================

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            bool goDark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Dark;
            ApplyTheme(goDark);
            settingManager.SetTheme(goDark ? "Dark" : "Light");
        }

        // 테마 + 강조색을 함께 적용한다. 시스템 강조색(updateAccent) 대신 목업 파랑을 쓴다.
        private void ApplyTheme(bool dark)
        {
            var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
            ApplicationAccentColorManager.Apply(AccentSeed, theme, systemGlassColor: false, systemAccentColor: false);
            ThemeIcon.Symbol = dark ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;
            ApplyAddButtonColors(dark);
            RefreshThemedBrushes();
        }

        // 코드에서 테마 리소스로 칠하는 요소(탭 색/활성 뱃지/베타 뱃지)를 현재 테마로 다시 칠한다.
        // (FindResource로 한 번 계산해 넣은 브러시는 DynamicResource와 달리 테마 전환 시 자동 갱신되지 않는다.)
        private void RefreshThemedBrushes()
        {
            ApplyTabBrushes();
            UpdateActivateStatus();
            SetBetaStatus(_betaStatusTextValue, _betaStatusApprovedValue);
        }

        // WPF-UI가 강조색에서 파생하는 Primary 색이 특히 라이트에서 탁해 보여서,
        // "새 단축키" 버튼 배경/hover/press를 또렷한 파랑으로 직접 지정한다.
        private void ApplyAddButtonColors(bool dark)
        {
            (string bg, string over, string press) = dark
                ? ("#2B88D8", "#3C97E4", "#2478C4")
                : ("#0F6CBD", "#1E7AC8", "#0C5C9E");
            AddButton.Background = HexBrush(bg);
            AddButton.MouseOverBackground = HexBrush(over);
            AddButton.PressedBackground = HexBrush(press);
            AddButton.Foreground = HexBrush("#FFFFFF");
        }

        private static System.Windows.Media.Brush HexBrush(string hex)
            => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        // ==================== 활성화 ====================

        private void ActivateToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isActive = true;
            _hotkeyManager.RegisterAll(new WindowInteropHelper(this).Handle);
            UpdateActivateStatus();
        }

        private void ActivateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isActive = false;
            _hotkeyManager.UnregisterAll(new WindowInteropHelper(this).Handle);
            UpdateActivateStatus();
        }

        private void UpdateActivateStatus()
        {
            ActivateStatusText.Text = _isActive ? "실행 중" : "중지됨";
            StatusPill.Background = (System.Windows.Media.Brush)FindResource(
                _isActive ? "SystemFillColorSuccessBackgroundBrush" : "SubtleFillColorSecondaryBrush");
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource(
                _isActive ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
        }

        // ==================== 자동 시작 / 자동 업데이트 ====================

        private void AutoStartToggle_Checked(object sender, RoutedEventArgs e)
        {
            SetAutoStart(true);
        }

        private void AutoStartToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAutoStart(false);
        }

        private void AutoUpdateToggle_Checked(object sender, RoutedEventArgs e)
        {
            settingManager.SetAutoUpdate(true);
        }

        private void AutoUpdateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            settingManager.SetAutoUpdate(false);
        }

        // ==================== 업데이트 ====================

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            var info = await updateManager.CheckAsync(await ShouldUseBetaChannelAsync());
            Logger.Info($"Update check - current:{info.Current}, latest:{info.Latest}, available:{info.UpdateAvailable}, failed:{info.Failed}");
            UpdateSettingsUpdateStatus(info);

            if (info.Failed)
            {
                await ShowAlert("업데이트 확인에 실패했습니다.\n네트워크 상태를 확인해주세요.", "업데이트 확인", confirm: false);
                return;
            }

            if (!info.UpdateAvailable)
            {
                await ShowAlert($"현재 최신 버전입니다. (v{info.Current})", "업데이트 확인", confirm: false);
                return;
            }

            await PromptAndInstallAsync(info);
        }

        // 설정 화면의 업데이트 상태 줄을 갱신한다.
        private void UpdateSettingsUpdateStatus(UpdateInfo info)
        {
            if (info.Failed)
                UpdateStatusText.Text = $"현재 v{info.Current} · 확인 실패";
            else if (info.UpdateAvailable)
                UpdateStatusText.Text = $"현재 v{info.Current} · 새 버전 v{info.Latest}";
            else
                UpdateStatusText.Text = $"현재 v{info.Current} · 최신 버전입니다";
        }

        // 새 버전 안내 → 동의 시 다운로드 → 앱 종료 → 업데이터가 교체·재실행.
        private async Task PromptAndInstallAsync(UpdateInfo info)
        {
            var ok = await ShowAlert(
                $"새 버전 v{info.Latest} 이(가) 있습니다. (현재 v{info.Current})\n지금 업데이트할까요?",
                "업데이트");
            if (!ok) return;

            // 자동 설치용 zip 에셋이 없으면 릴리스 페이지로 안내(수동 설치).
            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                await ShowAlert("자동 설치 패키지를 찾지 못했습니다.\n릴리스 페이지에서 직접 받아주세요.", "업데이트", confirm: false);
                updateManager.OpenReleasesPage();
                return;
            }

            try
            {
                var zipPath = Path.Combine(Path.GetTempPath(), "TextHotKey_update", "update.zip");

                var downloaded = await ShowDownloadDialogAsync(info.DownloadUrl, zipPath);
                if (!downloaded) return; // 사용자가 취소

                if (!updateManager.StartUpdater(zipPath))
                {
                    await ShowAlert("업데이터를 실행하지 못했습니다.\n릴리스 페이지에서 직접 받아주세요.", "업데이트", confirm: false);
                    updateManager.OpenReleasesPage();
                    return;
                }

                // 업데이터가 앱 종료를 기다렸다가 파일을 교체하고 다시 실행한다.
                _exitRequested = true;
                _trayIcon?.Dispose();
                _trayIcon = null;
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.Error($"Update install failed: {ex.Message}");
                await ShowAlert("업데이트 중 오류가 발생했습니다.\n" + ex.Message, "업데이트", confirm: false);
            }
        }

        // 다운로드 진행률 다이얼로그. 완료 시 true, 취소 시 false. 오류는 예외로 전달.
        private async Task<bool> ShowDownloadDialogAsync(string url, string zipPath)
        {
            var panel = new System.Windows.Controls.StackPanel { MinWidth = 280 };

            var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 6 };
            panel.Children.Add(bar);

            var pct = new System.Windows.Controls.TextBlock { Text = "0%", Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(pct);

            var cts = new CancellationTokenSource();
            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                pct.Text = $"{p * 100:0}%";
            });

            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "업데이트 다운로드 중…",
                Content = panel,
                CloseButtonText = "취소",
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
                Owner = this,
            };

            bool success = false;
            bool done = false;
            Exception? error = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    await updateManager.DownloadAsync(url, zipPath, progress, cts.Token);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(zipPath); } catch { /* 무시 */ }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done = true;
                    // MessageBox.Close()는 Obsolete로 표시돼 있으나 공개 대체 오버로드가 없어 그대로 사용한다.
#pragma warning disable CS0618
                    Dispatcher.Invoke(() => box.Close());
#pragma warning restore CS0618
                }
            });

            await box.ShowDialogAsync();

            // 다운로드가 끝나기 전에 닫혔다면(취소 버튼) 다운로드를 취소한다.
            if (!done) cts.Cancel();

            if (error != null) throw error;
            return success;
        }

        // ==================== 단축키 목록 ====================

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // DialogHost(Popup)는 한글 IME 조합이 깨지므로 실제 모달 Window로 입력받는다.
            var dialog = new AddHotkeyWindow { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _hotkeyManager.Add(
                    new WindowInteropHelper(this).Handle,
                    new HotkeyItem { Hotkey = dialog.HotkeyText, Text = dialog.InputText });
                HotkeyItems.Items.Refresh();
                UpdateHotkeyCount();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HotkeyItem item)
            {
                _hotkeyManager.Remove(new WindowInteropHelper(this).Handle, item);
                HotkeyItems.Items.Refresh();
                UpdateHotkeyCount();
            }
        }

        private void UpdateHotkeyCount()
        {
            int n = _hotkeyManager.HotkeyList.Count;
            CountText.Text = $"{n}개";
            EmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 탭 전환 ====================

        private void HotkeysTab_Click(object sender, RoutedEventArgs e)
        {
            ShowView(hotkeys: true);
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowView(hotkeys: false);
        }

        private void ShowView(bool hotkeys)
        {
            _hotkeysViewActive = hotkeys;
            HotkeysView.Visibility = hotkeys ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = hotkeys ? Visibility.Collapsed : Visibility.Visible;
            ApplyTabBrushes();
        }

        // 활성/비활성 탭의 글자·아이콘 색과 강조 바를 현재 테마·현재 뷰 기준으로 칠한다.
        private void ApplyTabBrushes()
        {
            bool hotkeys = _hotkeysViewActive;
            var active = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush");
            var inactive = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
            HotkeysTabButton.Foreground = hotkeys ? active : inactive;
            SettingsTabButton.Foreground = hotkeys ? inactive : active;
            HotkeysTabBar.Visibility = hotkeys ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabBar.Visibility = hotkeys ? Visibility.Collapsed : Visibility.Visible;
        }

        // ==================== 자동 시작 레지스트리 ====================

        private bool IsAutoStartEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("TextHotKey") != null;
        }

        private void SetAutoStart(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (enable)
                key?.SetValue("TextHotKey",
                    System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);
            else
                key?.DeleteValue("TextHotKey", false);
        }

        // ==================== 알림 다이얼로그 ====================

        // confirm=true  : 네/아니요 두 버튼(예: "지금 업데이트할까요?"). 반환값 = 네 선택 여부.
        // confirm=false : 확인 한 버튼짜리 단순 알림(예: "현재 최신 버전입니다").
        private async Task<bool> ShowAlert(string message, string title = "알림", bool confirm = true)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                Owner = this,
            };

            if (confirm)
            {
                box.PrimaryButtonText = "네";
                box.PrimaryButtonAppearance = ControlAppearance.Primary;
                box.CloseButtonText = "아니요";
            }
            else
            {
                box.CloseButtonText = "확인";
                box.IsPrimaryButtonEnabled = false;
                box.IsSecondaryButtonEnabled = false;
            }

            var result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
    }
}
