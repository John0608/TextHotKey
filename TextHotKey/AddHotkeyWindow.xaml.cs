using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TextHotKey
{
    // 단축키 추가 입력용 모달 창.
    // MaterialDesign DialogHost(Popup)나 AllowsTransparency 레이어드 창에서는
    // 한글/CJK IME 조합이 깨진다. WPF-UI FluentWindow는 비-레이어드라 IME가 정상 동작한다.
    public partial class AddHotkeyWindow : Wpf.Ui.Controls.FluentWindow
    {
        public string HotkeyText { get; private set; } = string.Empty;
        public string InputText => InputTextBox.Text;

        public AddHotkeyWindow()
        {
            InitializeComponent();
        }

        // 캡처 영역을 클릭하면 키 입력을 받도록 포커스를 준다.
        private void CaptureBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CaptureBox.Focus();
        }

        // 포커스 상태에 따라 강조 테두리를 켠다/끈다.
        private void CaptureBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            bool focused = CaptureBox.IsKeyboardFocusWithin;
            CaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource(
                focused ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            CaptureBox.BorderThickness = new Thickness(focused ? 1.4 : 1);
        }

        // 눌린 조합키+키를 "Ctrl+Shift+A" 형태로 캡처하고 키캡으로 렌더링한다.
        private void CaptureBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Esc(취소)/Tab(포커스 이동)은 기본 동작을 그대로 두어 창 조작을 막지 않는다.
            if (e.Key == Key.Escape || e.Key == Key.Tab) return;

            e.Handled = true;
            var keys = new List<string>();

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                keys.Add("Ctrl");
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                keys.Add("Alt");
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                keys.Add("Shift");

            // Alt 조합 시 실제 키는 SystemKey로 들어온다.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.LeftCtrl && key != Key.RightCtrl &&
                key != Key.LeftAlt && key != Key.RightAlt &&
                key != Key.LeftShift && key != Key.RightShift &&
                key != Key.System)
            {
                keys.Add(key.ToString());
            }

            HotkeyText = string.Join("+", keys);
            RenderKeycaps(keys);
        }

        private void RenderKeycaps(List<string> keys)
        {
            KeyCapPanel.Children.Clear();
            foreach (var k in keys)
            {
                KeyCapPanel.Children.Add(new Border
                {
                    Style = (Style)FindResource("Keycap"),
                    Child = new TextBlock
                    {
                        Text = k,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    }
                });
            }
            CapturePlaceholder.Visibility = keys.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // 단축키와 텍스트가 모두 입력됐을 때만 추가한다.
            if (string.IsNullOrWhiteSpace(HotkeyText) || string.IsNullOrWhiteSpace(InputTextBox.Text))
                return;

            DialogResult = true;
        }
    }
}
