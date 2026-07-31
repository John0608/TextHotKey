using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
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
                    var text = _hotkeyManager.HotkeyList[id].Text;
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() => TypeText(text));
                    });
                }
            }
            return IntPtr.Zero;
        }

        // 텍스트 입력
        private void TypeText(string text)
        {
            var sim = new WindowsInput.InputSimulator();
            sim.Keyboard.TextEntry(text);
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
            var curr = updateManager.GetCurrentVersion();
            var latest = await updateManager.GetLatestVersion();
            var checkUpdate = await updateManager.CheckForUpdates();
            
            if(checkUpdate)
            {
                var result = await ShowAlert($"새 버전이 있습니다. 업데이트 하시겠습니까?", "업데이트 확인");
                if (result is true)
                {
                    await updateManager.StartUpdater();
                }

            }

            Logger.Info($"Current version: {curr}, Latest version: {latest}");
        }

        // 추가 팝업
        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var view = new StackPanel { Margin = new Thickness(16), Width = 300 };

            var title = new TextBlock
            {
                Text = "단축키 추가",
                Style = (Style)FindResource("MaterialDesignHeadline6TextBlock"),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var hotkeyBox = new System.Windows.Controls.TextBox
            {
                Style = (Style)FindResource("MaterialDesignOutlinedTextBox"),
                Margin = new Thickness(0, 0, 0, 8),
                IsReadOnly = true
            };
            HintAssist.SetHint(hotkeyBox, "클릭 후 단축키를 누르세요");

            hotkeyBox.PreviewKeyDown += (s, args) =>
            {
                args.Handled = true;
                var keys = new List<string>();

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                    keys.Add("Ctrl");
                if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                    keys.Add("Alt");
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                    keys.Add("Shift");

                if (args.Key != Key.LeftCtrl && args.Key != Key.RightCtrl &&
                    args.Key != Key.LeftAlt && args.Key != Key.RightAlt &&
                    args.Key != Key.LeftShift && args.Key != Key.RightShift &&
                    args.Key != Key.System)
                {
                    keys.Add(args.Key.ToString());
                }

                if (keys.Count > 0)
                    hotkeyBox.Text = string.Join("+", keys);
            };

            var textBox = new System.Windows.Controls.TextBox
            {
                Style = (Style)FindResource("MaterialDesignOutlinedTextBox"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            HintAssist.SetHint(textBox, "입력될 텍스트");

            var buttons = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "취소",
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s, args) => DialogHost.Close("RootDialog", false);

            var confirmBtn = new System.Windows.Controls.Button
            {
                Content = "추가",
                Style = (Style)FindResource("MaterialDesignRaisedButton")
            };
            confirmBtn.Click += (s, args) => DialogHost.Close("RootDialog", true);

            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(confirmBtn);

            view.Children.Add(title);
            view.Children.Add(hotkeyBox);
            view.Children.Add(textBox);
            view.Children.Add(buttons);

            var result = await DialogHost.Show(view, "RootDialog");

            if (result is true &&
                !string.IsNullOrWhiteSpace(hotkeyBox.Text) &&
                !string.IsNullOrWhiteSpace(textBox.Text))
            {
                _hotkeyManager.Add(
                    new WindowInteropHelper(this).Handle,
                    new HotkeyItem { Hotkey = hotkeyBox.Text, Text = textBox.Text });
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

        private async Task<bool> ShowAlert(string message, string title = "알림")
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

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "아니요",
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s, e) => DialogHost.Close("RootDialog", false);

            var btn = new System.Windows.Controls.Button
            {
                Content = "네",
                Style = (Style)FindResource("MaterialDesignRaisedButton"),
            };
            btn.Click += (s, e) => DialogHost.Close("RootDialog", true);

            buttons.Children.Add(btn);
            buttons.Children.Add(cancelBtn);
            view.Children.Add(buttons);

            var result = await DialogHost.Show(view, "RootDialog");
            return result is true;
        }
    }
}