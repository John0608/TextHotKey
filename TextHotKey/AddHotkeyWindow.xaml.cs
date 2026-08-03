using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace TextHotKey
{
    // 단축키 추가 입력용 모달 창.
    // 기존에는 MaterialDesign DialogHost(내부적으로 Popup)로 띄웠는데,
    // WPF Popup 안 TextBox는 한글/CJK IME 조합이 깨진다. 실제 Window로 분리해 해결.
    public partial class AddHotkeyWindow : Window
    {
        public string HotkeyText => HotkeyBox.Text;
        public string InputText => InputTextBox.Text;

        public AddHotkeyWindow()
        {
            InitializeComponent();
        }

        // 단축키 입력란: 눌린 조합키+키를 "Ctrl+Shift+A" 형태로 캡처한다.
        private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;
            var keys = new List<string>();

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                keys.Add("Ctrl");
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                keys.Add("Alt");
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                keys.Add("Shift");

            if (e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl &&
                e.Key != Key.LeftAlt && e.Key != Key.RightAlt &&
                e.Key != Key.LeftShift && e.Key != Key.RightShift &&
                e.Key != Key.System)
            {
                keys.Add(e.Key.ToString());
            }

            if (keys.Count > 0)
                HotkeyBox.Text = string.Join("+", keys);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // 단축키와 텍스트가 모두 입력됐을 때만 추가한다.
            if (string.IsNullOrWhiteSpace(HotkeyBox.Text) || string.IsNullOrWhiteSpace(InputTextBox.Text))
                return;

            DialogResult = true;
        }
    }
}
