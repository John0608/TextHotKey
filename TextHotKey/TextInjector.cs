using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TextHotKey
{
    // 단축키로 설정된 텍스트를 활성 창에 입력한다.
    //  - 짧은 텍스트: 유니코드 키 입력 (붙여넣기를 지원하지 않는 곳에서도 동작)
    //  - 긴 텍스트: 클립보드에 넣고 Ctrl+V로 한 번에 붙여넣기 (길이와 무관하게 즉시 삽입)
    //
    // 단축키를 누른 순간에는 사용자가 조합키(Ctrl/Shift/Alt)를 아직 누르고 있다. 텍스트가 조합키와 섞이지 않도록
    // 입력 직전에 조합키를 뗀 것으로 보내고, 입력 직후 실제로 아직 누르고 있는 조합키를 다시 누른 것으로 돌려놓는다.
    // 돌려놓지 않으면 Windows는 조합키가 떼어진 것으로 알고 있어, 조합키를 누른 채 단축키 글자를 다시 누를 때
    // 단축키 대신 그 글자(예: d)가 그대로 입력된다.
    // 실제 눌림 여부는 저수준 키보드 훅으로 이 앱이 보낸 입력(표식 InjectMarker)을 뺀 나머지 입력을 따라가며 판단한다.
    // (화상 키보드·원격 데스크톱·다른 매크로 도구의 입력도 사용자의 입력으로 본다.)
    internal sealed class TextInjector : IDisposable
    {
        // 이 길이 이상이면 클립보드 붙여넣기, 미만이면 키 입력으로 처리한다.
        private const int ClipboardPasteThreshold = 20;

        // 이 앱이 SendInput으로 보내는 입력에 다는 표식(dwExtraInfo). 훅에서 자기 입력을 구분하는 데 쓴다.
        private static readonly IntPtr InjectMarker = new(0x54484B31); // "THK1"

        // 왼쪽/오른쪽을 구분한 조합키. 오른쪽 Ctrl/Alt는 확장 키다.
        private static readonly (int Vk, bool Extended)[] Modifiers =
        {
            (VK_LSHIFT, false), (VK_RSHIFT, false),
            (VK_LCONTROL, false), (VK_RCONTROL, true),
            (VK_LMENU, false), (VK_RMENU, true),
        };

        private readonly Dispatcher _dispatcher;
        private readonly bool[] _physicalDown = new bool[256];
        private readonly int[] _pressCount = new int[256];            // 키별 '새로 누름' 횟수(자동 반복 제외)
        private readonly Dictionary<int, int> _firedPress = new();    // 단축키 id → 마지막으로 실행한 누름 번호
        private readonly LowLevelKeyboardProc _hookProc;
        private IntPtr _hook = IntPtr.Zero;

        public TextInjector(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _hookProc = HookCallback; // GC 수거 방지를 위해 필드로 보관
        }

        // 사용자 키 추적 시작(단축키 활성화 시). UI 스레드에서 호출해야 한다(훅 콜백이 이 스레드 메시지 루프로 온다).
        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            foreach (var (vk, _) in Modifiers)
                _physicalDown[vk] = IsDown(vk); // 시작 시점에 이미 눌린 키
            using var module = Process.GetCurrentProcess().MainModule;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module?.ModuleName), 0);
            if (_hook == IntPtr.Zero)
                Logger.Warn($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _firedPress.Clear();
        }

        public void Dispose() => Stop();

        // 단축키 알림(WM_HOTKEY)을 실행할지. 같은 키를 한 번 누른 채로 오는 반복 알림은 무시한다.
        // MOD_NOREPEAT만으로는 부족하다: 입력 후 조합키를 다시 누른 것으로 돌려놓으면, Windows가 그 뒤의
        // 자동 반복을 새 누름으로 보고 단축키를 또 알린다. 훅이 없으면(설치 실패) 항상 실행한다.
        public bool ShouldFire(int hotkeyId, int vk)
        {
            if (_hook == IntPtr.Zero || vk is <= 0 or >= 256) return true;
            var press = _pressCount[vk];
            if (_firedPress.TryGetValue(hotkeyId, out var last) && last == press) return false;
            _firedPress[hotkeyId] = press;
            return true;
        }

        public void Send(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 지금 눌린 것으로 되어 있는 조합키(사용자 입력 + 이전에 이 앱이 다시 누른 것)
            var held = Modifiers.Where(m => IsDown(m.Vk)).ToList();

            var inputs = new List<INPUT>(text.Length * 2 + 16);
            foreach (var m in held)
                inputs.Add(MakeVkInput(m.Vk, keyUp: true, m.Extended));

            if (text.Length >= ClipboardPasteThreshold && TrySetClipboard(text, out var backup))
            {
                inputs.Add(MakeVkInput(VK_CONTROL, keyUp: false));
                inputs.Add(MakeVkInput(VK_V, keyUp: false));
                inputs.Add(MakeVkInput(VK_V, keyUp: true));
                inputs.Add(MakeVkInput(VK_CONTROL, keyUp: true));
                RestoreClipboardLater(backup);
            }
            else
            {
                foreach (char c in text)
                {
                    inputs.Add(MakeUnicodeInput(c, keyUp: false));
                    inputs.Add(MakeUnicodeInput(c, keyUp: true));
                }
            }

            // 사용자가 아직 누르고 있는 조합키는 다시 누른 상태로 돌려놓는다.
            var repressed = held.Where(m => _hook == IntPtr.Zero || _physicalDown[m.Vk]).ToList();
            foreach (var m in repressed)
                inputs.Add(MakeVkInput(m.Vk, keyUp: false, m.Extended));

            // Alt를 다시 누른 뒤 사용자가 Alt만 떼면 앱 메뉴가 활성화된다. 의미 없는 키를 한 번 눌러 이를 막는다.
            if (repressed.Any(m => m.Vk is VK_LMENU or VK_RMENU))
            {
                inputs.Add(MakeVkInput(VK_NONE, keyUp: false));
                inputs.Add(MakeVkInput(VK_NONE, keyUp: true));
            }

            var array = inputs.ToArray();
            SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());

            if (repressed.Count > 0 && _hook != IntPtr.Zero)
                ReleaseIfLetGoLater(repressed);
        }

        // 다시 누른 직후(같은 순간) 사용자가 손을 뗐다면, 사용자의 keyup이 주입한 keydown보다 먼저 처리돼
        // 조합키가 눌린 채로 남는다. 잠시 뒤 사용자가 뗀 키가 눌린 것으로 남아 있으면 떼어 준다.
        private async void ReleaseIfLetGoLater(List<(int Vk, bool Extended)> repressed)
        {
            foreach (var delay in new[] { 50, 250 })
            {
                await Task.Delay(delay);
                var stuck = repressed.Where(m => !_physicalDown[m.Vk] && IsDown(m.Vk)).ToList();
                if (stuck.Count == 0) continue;
                Logger.Info($"Releasing stuck modifiers: {string.Join(",", stuck.Select(m => m.Vk.ToString("X2")))}");
                var ups = stuck.Select(m => MakeVkInput(m.Vk, keyUp: true, m.Extended)).ToArray();
                SendInput((uint)ups.Length, ups, Marshal.SizeOf<INPUT>());
            }
        }

        private static bool TrySetClipboard(string text, out string? backup)
        {
            backup = null;
            try { if (System.Windows.Clipboard.ContainsText()) backup = System.Windows.Clipboard.GetText(); }
            catch { /* 백업 실패는 무시 */ }
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false; // 클립보드 사용 실패 → 키 입력 방식으로 대체
            }
        }

        // 대상 앱이 붙여넣기를 처리할 시간을 준 뒤 원래 클립보드를 복원한다.
        private void RestoreClipboardLater(string? backup)
        {
            if (backup == null) return;
            Task.Delay(300).ContinueWith(_ =>
                _dispatcher.Invoke(() => { try { System.Windows.Clipboard.SetText(backup); } catch { } }));
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (info.dwExtraInfo != InjectMarker && info.vkCode < 256)
                {
                    int msg = wParam.ToInt32();
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        if (!_physicalDown[(int)info.vkCode]) _pressCount[(int)info.vkCode]++; // 자동 반복은 세지 않음
                        _physicalDown[(int)info.vkCode] = true;
                    }
                    else if (msg is WM_KEYUP or WM_SYSKEYUP) _physicalDown[(int)info.vkCode] = false;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0), dwExtraInfo = InjectMarker } }
        };

        private static INPUT MakeVkInput(int vk, bool keyUp, bool extended = false) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                    dwExtraInfo = InjectMarker,
                }
            }
        };

        // ===== 인터롭 =====

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const int VK_CONTROL = 0x11;
        private const int VK_V = 0x56;
        private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        private const int VK_NONE = 0xE8; // 할당되지 않은 가상 키(메뉴 활성화 방지용)

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

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
    }
}
