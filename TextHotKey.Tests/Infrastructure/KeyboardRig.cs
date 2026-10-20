using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TextHotKey.Tests.Infrastructure;

// 텍스트 상자 하나짜리 창을 맨 앞에 띄우고, 정해진 시각(ms)에 키를 누르거나 뗀다(SendInput).
// 앱이 입력한 텍스트가 언제 어떻게 바뀌었는지 기록한다. 실제 사용자의 키 입력을 흉내 내는 용도.
internal static class KeyboardRig
{
    public const int Ctrl = 0xA2, Shift = 0xA0, Alt = 0xA4;

    public record Step(int AtMs, int Vk, bool Up);
    public record TextChange(long AtMs, string Text);
    public record Result(IReadOnlyList<TextChange> Changes, IReadOnlyList<(long AtMs, Step Step)> Sent, int[] StuckModifiers)
    {
        public string FinalText => Changes.Count == 0 ? "" : Changes[^1].Text;

        // 해당 키를 처음 누른 시각부터 텍스트가 처음 바뀐 시각까지(ms). 바뀌지 않았으면 null.
        public long? FirstTextDelayAfter(int vk)
        {
            var down = Sent.FirstOrDefault(s => s.Step.Vk == vk && !s.Step.Up);
            var change = Changes.FirstOrDefault(c => c.AtMs >= down.AtMs);
            return change == null ? null : change.AtMs - down.AtMs;
        }

        public override string ToString() =>
            string.Join("\n", Sent.Select(s => (s.AtMs, $"{(s.Step.Up ? "up  " : "down")} 0x{s.Step.Vk:X2}"))
                .Concat(Changes.Select(c => (c.AtMs, $"text=\"{c.Text}\"")))
                .OrderBy(x => x.AtMs)
                .Select(x => $"{x.AtMs,5}ms  {x.Item2}"))
            + $"\nstuck modifiers: [{string.Join(", ", StuckModifiers.Select(v => $"0x{v:X2}"))}]";
    }

    public static Step Down(int at, int vk) => new(at, vk, false);
    public static Step Up(int at, int vk) => new(at, vk, true);

    // 조합키를 누른 채 key를 여러 번 누르는 등 시나리오를 만든다: 조합키 down → keyPresses → releaseAt에 조합키 up.
    public static List<Step> Hold(int[] modifiers, IEnumerable<Step> keySteps, int releaseAt)
    {
        var steps = modifiers.Select((m, i) => Down(i * 5, m)).ToList();
        steps.AddRange(keySteps);
        steps.AddRange(modifiers.AsEnumerable().Reverse().Select(m => Up(releaseAt, m)));
        return steps;
    }

    public static Result Run(IEnumerable<Step> steps, int observeMs)
    {
        Result? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = RunOnSta(steps.OrderBy(s => s.AtMs).ToList(), observeMs); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result!;
    }

    private static Result RunOnSta(List<Step> steps, int observeMs)
    {
        var changes = new List<TextChange>();
        var sent = new List<(long, Step)>();
        var sw = new Stopwatch();
        using var form = new Form { Text = "THK keyboard rig", Width = 600, Height = 200, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(box);
        box.TextChanged += (_, _) => changes.Add(new(sw.ElapsedMilliseconds, box.Text));

        var queue = new Queue<Step>(steps);
        var timer = new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick += (_, _) =>
        {
            while (queue.Count > 0 && sw.ElapsedMilliseconds >= queue.Peek().AtMs)
            {
                var s = queue.Dequeue();
                Key(s.Vk, s.Up);
                sent.Add((sw.ElapsedMilliseconds, s));
            }
            if (queue.Count == 0 && sw.ElapsedMilliseconds > observeMs)
            {
                timer.Stop();
                form.Close();
            }
        };
        form.Shown += (_, _) =>
        {
            ForceForeground(form.Handle);
            box.Focus();
            Thread.Sleep(300);
            sw.Start();
            timer.Start();
        };
        Application.Run(form);

        // 시나리오가 끝난 뒤 눌린 것으로 남은 조합키(= 앱이 남긴 '고착' 키)를 기록하고 정리한다.
        Thread.Sleep(400); // 앱의 지연 정리(최대 300ms)가 끝날 시간
        var stuck = new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0).ToArray();
        foreach (var vk in stuck) Key(vk, up: true);
        return new Result(changes, sent, stuck);
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        // 다른 프로세스가 전경일 때 SetForegroundWindow가 거부되지 않도록 Alt 탭 흉내(할당 안 된 키)를 먼저 보낸다.
        Key(0xE8, up: false);
        Key(0xE8, up: true);
        SetForegroundWindow(hwnd);
    }

    private static void Key(int vk, bool up)
    {
        var input = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? 2u : 0u } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
}
