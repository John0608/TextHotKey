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
        private readonly TextInjector _textInjector;
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

        // 베타 신청 상태 머신.
        private enum BetaState { None, Pending, Approved }
        private BetaState _betaState = BetaState.None;

        public MainWindow()
        {
            InitializeComponent();
            _textInjector = new TextInjector(Dispatcher);
            betaManager = new BetaManager(settingManager);

            // 저장된 테마 + 커스텀 강조색 적용.
            ApplyTheme(settingManager.GetTheme());

            HotkeyItems.ItemsSource = _hotkeyManager.HotkeyList;
            UpdateHotkeyCount();
            ShowView(hotkeys: true); // 시작 탭(단축키) 강조 초기화

            AutoStartToggle.IsChecked = IsAutoStartEnabled();
            AutoUpdateToggle.IsChecked = settingManager.GetAutoUpdate();
            UpdateStatusText.Text = $"현재 v{updateManager.GetCurrentVersion()}";

            // 테스트(베타) 프로그램 UI 초기화. (승인 여부는 Loaded에서 서버 확인 후 갱신)
            BetaCodeText.Text = betaManager.GetDeviceCode();
            BetaEmailBox.Text = betaManager.GetEmail();
            BetaUpdateToggle.IsChecked = settingManager.GetBetaOptIn();
            ApplyBetaState(settingManager.GetBetaRequested() ? BetaState.Pending : BetaState.None);

            SetupTrayIcon();
            Closing += Window_Closing;
            StateChanged += Window_StateChanged;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 베타 승인 상태를 조용히 갱신(백그라운드).
            _ = RefreshBetaStatusAsync();

            // 지난 업데이트가 실패해 구버전으로 되돌아온 경우 알린다.
            // 이번 실행에선 자동 업데이트 안내를 생략한다(실패 → 재실행 → 즉시 재시도 반복 방지).
            if (updateManager.TakeFailedUpdateMessage() is { } failure)
            {
                var open = await ShowAlert(
                    $"업데이트에 실패해 이전 버전을 계속 사용합니다.\n{failure}\n\n릴리스 페이지에서 직접 설치할 수 있습니다. 지금 열까요?",
                    "업데이트 실패");
                if (open) updateManager.OpenReleasesPage();
                return;
            }

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
            _textInjector.Dispose();
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

        // 서버 승인 여부 + 로컬 신청 여부로 상태를 계산해 UI를 갱신한다.
        //  승인됨   : 서버가 approved=true
        //  승인 대기중 : 신청은 했지만 아직 미승인
        //  미신청    : 신청 이력 없음
        private async Task RefreshBetaStatusAsync()
        {
            ShowBetaChecking();
            bool approved = await betaManager.IsApprovedAsync();
            _betaApproved = approved;

            var state = approved ? BetaState.Approved
                      : settingManager.GetBetaRequested() ? BetaState.Pending
                      : BetaState.None;
            ApplyBetaState(state);
        }

        // 상태별 뱃지 색/문구 + 이메일 잠금 + 신청 버튼/베타 토글 활성화를 일괄 적용한다.
        private void ApplyBetaState(BetaState state)
        {
            _betaState = state;

            string text, bgKey, fgKey;
            bool emailLocked, canRequest, toggleEnabled;

            switch (state)
            {
                case BetaState.Approved:
                    text = "승인됨 ✓";
                    bgKey = "SystemFillColorSuccessBackgroundBrush"; fgKey = "SystemFillColorSuccessBrush";
                    emailLocked = true; canRequest = false; toggleEnabled = true;
                    break;
                case BetaState.Pending:
                    text = "승인 대기중";
                    bgKey = "SystemFillColorCautionBackgroundBrush"; fgKey = "SystemFillColorCautionBrush";
                    emailLocked = true; canRequest = false; toggleEnabled = false;
                    break;
                default: // None
                    text = "미신청";
                    bgKey = "SubtleFillColorSecondaryBrush"; fgKey = "TextFillColorSecondaryBrush";
                    emailLocked = false; canRequest = true; toggleEnabled = false;
                    break;
            }

            BetaStatusText.Text = text;
            BetaStatusPill.Background = ThemeBrush(bgKey, "SubtleFillColorSecondaryBrush");
            BetaStatusText.Foreground = ThemeBrush(fgKey, "TextFillColorSecondaryBrush");
            BetaEmailBox.IsReadOnly = emailLocked;
            BetaRequestButton.IsEnabled = canRequest;
            BetaUpdateToggle.IsEnabled = toggleEnabled;
            // "참가 그만두기"는 참가 중(대기중/승인됨)일 때만 노출.
            BetaLeaveButton.Visibility = state == BetaState.None ? Visibility.Collapsed : Visibility.Visible;
        }

        // 서버 확인 중 임시 표시(중립).
        private void ShowBetaChecking()
        {
            BetaStatusText.Text = "확인 중…";
            BetaStatusPill.Background = ThemeBrush("SubtleFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush");
            BetaStatusText.Foreground = ThemeBrush("TextFillColorSecondaryBrush", "TextFillColorSecondaryBrush");
        }

        // 리소스 키가 없으면 폴백으로 안전하게 브러시를 얻는다(테마별 키 차이 방지).
        private System.Windows.Media.Brush ThemeBrush(string key, string fallback)
            => (System.Windows.Media.Brush)(TryFindResource(key) ?? FindResource(fallback));

        // 테스트 신청: 코드·이메일을 Supabase에 바로 제출한다(브라우저·계정 불필요).
        private async void BetaRequestButton_Click(object sender, RoutedEventArgs e)
        {
            BetaRequestButton.IsEnabled = false; // 중복 클릭 방지
            var email = BetaEmailBox.Text.Trim();
            var ok = await betaManager.SubmitRequestAsync(email);

            if (ok)
            {
                settingManager.SetBetaRequested(true);
                await ShowAlert("테스트 신청이 접수되었습니다.\n오너 승인 후 자동으로 반영됩니다.",
                    "테스트 신청", confirm: false);
            }
            else
            {
                await ShowAlert("신청 전송에 실패했습니다.\n네트워크 상태를 확인한 뒤 다시 시도해주세요.",
                    "테스트 신청", confirm: false);
            }

            await RefreshBetaStatusAsync();
        }

        private async void BetaRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshBetaStatusAsync();
        }

        // 참가 완전 탈퇴: 확인 후 서버에서 신청/승인 삭제 + 로컬 리셋 → 미신청으로.
        private async void BetaLeaveButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await ShowAlert(
                "테스트(베타) 참가를 그만둘까요?\n승인이 해제되고 미신청 상태로 돌아갑니다. (다시 하려면 재신청)",
                "참가 그만두기");
            if (!ok) return;

            var left = await betaManager.LeaveAsync();
            if (left)
            {
                settingManager.SetBetaRequested(false);
                settingManager.SetBetaOptIn(false);
                BetaUpdateToggle.IsChecked = false;
                // 완전 탈퇴: 참가코드도 새로 발급 → 재신청 시 새 신원으로 시작.
                betaManager.ResetDeviceCode();
                BetaCodeText.Text = betaManager.GetDeviceCode();
                await ShowAlert("테스트 참가를 종료했습니다.\n참가코드가 새로 발급되었습니다.", "참가 그만두기", confirm: false);
            }
            else
            {
                await ShowAlert("처리에 실패했습니다.\n네트워크 상태를 확인한 뒤 다시 시도해주세요.",
                    "참가 그만두기", confirm: false);
            }

            await RefreshBetaStatusAsync();
        }

        private async void BetaUpdateToggle_Checked(object sender, RoutedEventArgs e)
        {
            settingManager.SetBetaOptIn(true);
            await RefreshUpdateStatusAsync(); // 베타 채널로 바뀌었으니 업데이트 상태 재조회
        }

        private async void BetaUpdateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            settingManager.SetBetaOptIn(false);
            await RefreshUpdateStatusAsync(); // 안정판 채널로 되돌았으니 사전릴리스 문구 제거
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
                    var item = _hotkeyManager.HotkeyList[id];
                    var (_, vk) = _hotkeyManager.ParseHotkey(item.Hotkey);
                    if (_textInjector.ShouldFire(id, (int)vk))
                        SendText(item.Text);
                }
            }
            return IntPtr.Zero;
        }

        // 텍스트 입력 (조합키 처리·사용자 키 추적은 TextInjector 참고)
        private void SendText(string text)
        {
            try
            {
                _textInjector.Send(text);
            }
            catch (Exception ex)
            {
                Logger.Error($"SendText failed: {ex.Message}");
            }
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
            ApplyBetaState(_betaState);
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
            _textInjector.Start();
            _hotkeyManager.RegisterAll(new WindowInteropHelper(this).Handle);
            UpdateActivateStatus();
        }

        private void ActivateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isActive = false;
            _hotkeyManager.UnregisterAll(new WindowInteropHelper(this).Handle);
            _textInjector.Stop();
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

        // 현재 채널(안정판/베타) 기준으로 업데이트 상태 텍스트만 다시 조회한다(설치 안내 없이).
        // 베타 참가/탈퇴·토글로 채널이 바뀔 때 사전릴리스 문구를 즉시 반영/제거하기 위함.
        private async Task RefreshUpdateStatusAsync()
        {
            var info = await updateManager.CheckAsync(await ShouldUseBetaChannelAsync());
            UpdateSettingsUpdateStatus(info);
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
                var zipPath = Path.Combine(AppEnv.UpdateTempDir, "update.zip");

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
        //
        // WPF-UI MessageBox는 프로그램적으로 닫는 방법(Close()/CancellationToken)이 모두
        // 내부 TaskCompletionSource를 완료시키지 못해 100% 후에도 다이얼로그가 안 닫히는 버그가 있었다.
        // 그래서 표준 Window + Window.Close()(=ShowDialog 확실히 반환)로 직접 구현한다.
        private async Task<bool> ShowDownloadDialogAsync(string url, string zipPath)
        {
            var title = new System.Windows.Controls.TextBlock
            {
                Text = "업데이트 다운로드 중…",
                FontSize = 14,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 6 };
            var pct = new System.Windows.Controls.TextBlock { Text = "0%", Margin = new Thickness(0, 8, 0, 12) };
            var cancelBtn = new Wpf.Ui.Controls.Button
            {
                Content = "취소",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Padding = new Thickness(16, 4, 16, 4)
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(title);
            panel.Children.Add(bar);
            panel.Children.Add(pct);
            panel.Children.Add(cancelBtn);

            var win = new Wpf.Ui.Controls.FluentWindow
            {
                Title = "업데이트",
                Owner = this,
                Width = 340,
                SizeToContent = System.Windows.SizeToContent.Height,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None,
                Content = panel,
            };

            var downloadCts = new CancellationTokenSource();
            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                pct.Text = $"{p * 100:0}%";
            });

            bool success = false;
            Exception? error = null;

            cancelBtn.Click += (_, _) =>
            {
                downloadCts.Cancel();
                win.Close();
            };

            // 창이 실제로 뜬 뒤 다운로드를 시작해야, 빠른 다운로드에서 win.Close()가 표시 전에
            // 호출돼 무시되는 레이스를 막는다.
            Task? downloadTask = null;
            win.ContentRendered += (_, _) =>
            {
                downloadTask = Task.Run(async () =>
                {
                    try
                    {
                        await updateManager.DownloadAsync(url, zipPath, progress, downloadCts.Token);
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
                        // 완료·실패 시 진행 다이얼로그를 닫는다(UI 스레드에서). 이미 닫혔으면 무시.
                        win.Dispatcher.Invoke(() => { if (win.IsLoaded) win.Close(); });
                    }
                });
            };

            // 모달로 표시. 다운로드 완료(win.Close) 또는 사용자 취소 시 반환된다.
            win.ShowDialog();
            if (downloadTask != null) await downloadTask;

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
