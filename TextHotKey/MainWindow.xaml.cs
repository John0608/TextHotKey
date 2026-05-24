using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace TextHotKey
{
    public partial class MainWindow : Window
    {
        private List<HotkeyItem> _hotkeyList = new List<HotkeyItem>();
        private bool _isActive = false;

        // Win32 API
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CTRL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        public MainWindow()
        {
            InitializeComponent();
            HotkeyListView.ItemsSource = _hotkeyList;
            AutoStartCheckBox.IsChecked = IsAutoStartEnabled();
            ThemeToggle.IsChecked = true;
            LoadHotkeys();
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
                if (id >= 0 && id < _hotkeyList.Count)
                {
                    handled = true;
                    var text = _hotkeyList[id].Text;
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() => TypeText(text));
                    });
                }
            }
            return IntPtr.Zero;
        }

        // 텍스트 입력


        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private void TypeText(string text)
        {
            var sim = new WindowsInput.InputSimulator();
            sim.Keyboard.TextEntry(text);
        }

        // 단축키 등록
        private void RegisterAllHotkeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            for (int i = 0; i < _hotkeyList.Count; i++)
            {
                var item = _hotkeyList[i];
                var (modifiers, vk) = ParseHotkey(item.Hotkey);
                bool success = RegisterHotKey(handle, i, modifiers, vk);
            }
        }

        // 단축키 해제
        private void UnregisterAllHotkeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            for (int i = 0; i < _hotkeyList.Count; i++)
                UnregisterHotKey(handle, i);
        }

        // 단축키 파싱 (예: "Ctrl+Alt+A")
        private (uint modifiers, uint vk) ParseHotkey(string hotkey)
        {
            uint modifiers = 0;
            uint vk = 0;
            var parts = hotkey.Split('+');

            foreach (var part in parts)
            {
                switch (part.Trim().ToUpper())
                {
                    case "CTRL": modifiers |= MOD_CTRL; break;
                    case "ALT": modifiers |= MOD_ALT; break;
                    case "SHIFT": modifiers |= MOD_SHIFT; break;
                    default:
                        if (Enum.TryParse<Key>(part.Trim(), true, out var key))
                            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                        break;
                }
            }
            return (modifiers, vk);
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
            UnregisterAllHotkeys();
            Close();
        }

        // 다크/라이트 모드
        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            var paletteHelper = new PaletteHelper();
            var theme = paletteHelper.GetTheme();
            theme.SetBaseTheme(BaseTheme.Dark);
            paletteHelper.SetTheme(theme);
        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            var paletteHelper = new PaletteHelper();
            var theme = paletteHelper.GetTheme();
            theme.SetBaseTheme(BaseTheme.Light);
            paletteHelper.SetTheme(theme);
        }

        // 활성화 토글
        private void ActivateToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isActive = true;
            RegisterAllHotkeys();
        }

        private void ActivateToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isActive = false;
            UnregisterAllHotkeys();
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
                // 활성화 중이면 기존 단축키 해제 후 재등록
                if (_isActive) UnregisterAllHotkeys();

                _hotkeyList.Add(new HotkeyItem
                {
                    Hotkey = hotkeyBox.Text,
                    Text = textBox.Text
                });
                HotkeyListView.Items.Refresh();
                SaveHotkeys();

                if (_isActive) RegisterAllHotkeys();
            }
        }

        // 삭제
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HotkeyItem item)
            {
                if (_isActive) UnregisterAllHotkeys();
                _hotkeyList.Remove(item);
                HotkeyListView.Items.Refresh();
                SaveHotkeys(); 
                if (_isActive) RegisterAllHotkeys();
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

        // 저장 경로
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TextHotKey", "hotkeys.json");

        // 저장
        private void SaveHotkeys()
        {
            var dir = Path.GetDirectoryName(SavePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_hotkeyList);
            File.WriteAllText(SavePath, json);
        }

        // 불러오기
        private void LoadHotkeys()
        {
            if (!File.Exists(SavePath)) return;

            var json = File.ReadAllText(SavePath);
            var list = JsonSerializer.Deserialize<List<HotkeyItem>>(json);
            if (list != null)
            {
                _hotkeyList.Clear();
                _hotkeyList.AddRange(list);
                HotkeyListView.Items.Refresh();
            }
        }
    }

    public class HotkeyItem
    {
        public string Hotkey { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}