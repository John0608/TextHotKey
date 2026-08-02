using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace TextHotKey
{
    public partial class MainWindow : Window
    {
        private bool _isActive = false;
        private readonly HotkeyManager _hotkeyManager = new HotkeyManager();
        private readonly SettingManager settingManager = new SettingManager();
        private readonly UpdateManager updateManager = new UpdateManager();

        public MainWindow()
        {
            InitializeComponent();
            HotkeyListView.ItemsSource = _hotkeyManager.HotkeyList;
            AutoStartCheckBox.IsChecked = IsAutoStartEnabled();
            ThemeToggle.IsChecked = settingManager.GetTheme();
            AutoUpdateCheckBox.IsChecked = settingManager.GetAutoUpdate();

            // "프로그램 시작 시 자동 업데이트"가 켜져 있으면 시작 직후 조용히 확인한다.
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!settingManager.GetAutoUpdate()) return;

            var info = await updateManager.CheckAsync();
            Logger.Info($"Startup update check - current:{info.Current}, latest:{info.Latest}, available:{info.UpdateAvailable}, failed:{info.Failed}");

            // 시작 시엔 실패/최신이면 조용히 넘어가고, 새 버전이 있을 때만 안내한다.
            if (info.Failed || !info.UpdateAvailable) return;

            await PromptAndInstallAsync(info);
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

        // 타이틀바 드래그
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        // 최소화
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // 닫기
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _hotkeyManager.UnregisterAll(new WindowInteropHelper(this).Handle);
            Close();
        }

        // 다크/라이트 모드
        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            var paletteHelper = new PaletteHelper();
            var theme = paletteHelper.GetTheme();
            theme.SetBaseTheme(BaseTheme.Dark);
            paletteHelper.SetTheme(theme);
            
            settingManager.SetTheme("Dark");
        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            var paletteHelper = new PaletteHelper();
            var theme = paletteHelper.GetTheme();
            theme.SetBaseTheme(BaseTheme.Light);
            paletteHelper.SetTheme(theme);

            settingManager.SetTheme("Light");
        }

        // 활성화 토글
        private void ActivateToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isActive = true;
            _hotkeyManager.RegisterAll(new WindowInteropHelper(this).Handle);
        }

        private void ActivateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isActive = false;
            _hotkeyManager.UnregisterAll(new WindowInteropHelper(this).Handle);
        }

        // 자동 시작
        private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SetAutoStart(true);
        }

        private void AutoStartCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAutoStart(false);
        }

        private void AutoUpdateCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            settingManager.SetAutoUpdate(true);
        }

        private void AutoUpdateCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            settingManager.SetAutoUpdate(false);
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            var info = await updateManager.CheckAsync();
            Logger.Info($"Update check - current:{info.Current}, latest:{info.Latest}, available:{info.UpdateAvailable}, failed:{info.Failed}");

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
            var view = new StackPanel { Margin = new Thickness(16), Width = 300 };

            view.Children.Add(new TextBlock
            {
                Text = "업데이트 다운로드 중...",
                Style = (Style)FindResource("MaterialDesignHeadline6TextBlock"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 8 };
            view.Children.Add(bar);

            var pct = new TextBlock { Text = "0%", Margin = new Thickness(0, 8, 0, 16) };
            view.Children.Add(pct);

            var cts = new CancellationTokenSource();
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "취소",
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            cancelBtn.Click += (s, e) => cts.Cancel();
            view.Children.Add(cancelBtn);

            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                pct.Text = $"{p * 100:0}%";
            });

            bool success = false;
            Exception? error = null;

            await DialogHost.Show(view, "RootDialog", async (object s, DialogOpenedEventArgs args) =>
            {
                try
                {
                    await updateManager.DownloadAsync(url, zipPath, progress, cts.Token);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    // 사용자 취소 → 부분 파일 정리.
                    try { File.Delete(zipPath); } catch { /* 무시 */ }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    args.Session.Close(false);
                }
            });

            if (error != null) throw error;
            return success;
        }

        // 추가 팝업
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // DialogHost(Popup)는 한글 IME 조합이 깨지므로 실제 모달 Window로 입력받는다.
            var dialog = new AddHotkeyWindow { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _hotkeyManager.Add(
                    new WindowInteropHelper(this).Handle,
                    new HotkeyItem { Hotkey = dialog.HotkeyText, Text = dialog.InputText });
                HotkeyListView.Items.Refresh();
            }
        }

        // 삭제
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HotkeyItem item)
            {
                _hotkeyManager.Remove(new WindowInteropHelper(this).Handle, item);
                HotkeyListView.Items.Refresh();
            }
        }

        // 탭 전환
        private void HomeTab_Click(object sender, RoutedEventArgs e)
        {
            HomePage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            HomePage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        }

        // 자동 시작 레지스트리
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

        // confirm=true  : 네/아니요 두 버튼(예: "지금 업데이트할까요?"). 반환값 = 네 선택 여부.
        // confirm=false : 확인 한 버튼짜리 단순 알림(예: "현재 최신 버전입니다").
        private async Task<bool> ShowAlert(string message, string title = "알림", bool confirm = true)
        {
            var view = new StackPanel { Margin = new Thickness(16), Width = 250 };

            view.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)FindResource("MaterialDesignHeadline6TextBlock"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            view.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var buttons = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            if (confirm)
            {
                var cancelBtn = new System.Windows.Controls.Button
                {
                    Content = "아니요",
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                cancelBtn.Click += (s, e) => DialogHost.Close("RootDialog", false);

                var okBtn = new System.Windows.Controls.Button
                {
                    Content = "네",
                    Style = (Style)FindResource("MaterialDesignRaisedButton"),
                };
                okBtn.Click += (s, e) => DialogHost.Close("RootDialog", true);

                buttons.Children.Add(okBtn);
                buttons.Children.Add(cancelBtn);
            }
            else
            {
                var okBtn = new System.Windows.Controls.Button
                {
                    Content = "확인",
                    Style = (Style)FindResource("MaterialDesignRaisedButton"),
                };
                okBtn.Click += (s, e) => DialogHost.Close("RootDialog", true);

                buttons.Children.Add(okBtn);
            }
            view.Children.Add(buttons);

            var result = await DialogHost.Show(view, "RootDialog");
            return result is true;
        }
    }
}