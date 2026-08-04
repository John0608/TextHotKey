using System.Globalization;
using System.Windows.Data;

namespace TextHotKey
{
    // "Ctrl+Alt+M" -> ["Ctrl", "Alt", "M"] : 단축키를 키캡 칩으로 렌더링하기 위한 변환기.
    public class HotkeyToKeysConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            return text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
