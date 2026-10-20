using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using TextHotKey.Tests.Infrastructure;
using Xunit.Abstractions;
using static TextHotKey.Tests.Infrastructure.KeyboardRig;

namespace TextHotKey.Tests.E2E;

// 실제 앱에 단축키를 등록하고, 사용자의 키 입력을 흉내 내 텍스트가 어떻게 입력되는지 본다.
// 회귀 방지 대상(1.4.0-beta.2에서 재현):
//  - 조합키를 누르고 있는 동안 0.5초 대기 후 입력(반응 느림)
//  - 입력 후 조합키를 '뗀 것'으로 남겨, 조합키를 누른 채 단축키 글자를 다시 누르면 그 글자(d)가 입력됨
//  - 키를 꾹 누르면 자동 반복으로 텍스트가 여러 번 입력됨
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public class HotkeyInputTests(ReleaseBuildsFixture builds, ITestOutputHelper output)
{
    private const int K = 0x4B, J = 0x4A, L = 0x4C;
    private const string Short = "HELLO";
    private const string Long = "LONG-TEXT-VIA-CLIPBOARD-0123456789"; // 20자 이상 → 클립보드 붙여넣기 경로

    private static readonly int[] CtrlShift = [Ctrl, Shift];

    [Fact]
    public void HoldingModifiers_TextIsInsertedImmediately()
    {
        var r = RunWithApp(Hold(CtrlShift, [Down(30, K), Up(80, K)], releaseAt: 1000), 1200);

        Assert.Equal(Short, r.FinalText);
        var delay = r.FirstTextDelayAfter(K);
        Assert.True(delay is < 200, $"단축키를 누른 뒤 {delay}ms 후에야 입력됨(조합키를 뗄 때까지 기다림?)");
        Assert.Empty(r.StuckModifiers);
    }

    [Fact]
    public void PressingKeyAgainWhileHoldingModifiers_InsertsTextAgain_NotTheLetter()
    {
        var keys = new[] { 30, 280, 730, 930 }.SelectMany(t => new[] { Down(t, K), Up(t + 30, K) });
        var r = RunWithApp(Hold(CtrlShift, keys, releaseAt: 1200), 1400);

        Assert.Equal(string.Concat(Enumerable.Repeat(Short, 4)), r.FinalText);
        Assert.Empty(r.StuckModifiers);
    }

    [Fact]
    public void HoldingHotkeyDown_InsertsTextOnce()
    {
        // 키보드 자동 반복: 같은 키의 keydown이 keyup 없이 33ms 간격으로 반복된다.
        var repeats = Enumerable.Range(0, 30).Select(i => Down(30 + i * 33, K)).Append(Up(1030, K));
        var r = RunWithApp(Hold(CtrlShift, repeats, releaseAt: 1050), 1300);

        Assert.Equal(Short, r.FinalText);
        Assert.Empty(r.StuckModifiers);
    }

    [Fact]
    public void LongText_ViaClipboard_WhileHoldingModifiers()
    {
        var keys = new[] { 30, 400 }.SelectMany(t => new[] { Down(t, J), Up(t + 30, J) });
        var r = RunWithApp(Hold(CtrlShift, keys, releaseAt: 800), 1000);

        Assert.Equal(Long + Long, r.FinalText);
        Assert.Empty(r.StuckModifiers);
    }

    // 단축키를 누르자마자 조합키를 떼는 경우(입력과 거의 같은 순간) 조합키가 눌린 채로 남으면 안 된다.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    public void ReleasingModifiersRightAway_LeavesNoStuckKeys(int releaseAfterMs)
    {
        var r = RunWithApp(Hold(CtrlShift, [Down(30, K), Up(30 + releaseAfterMs, K)], releaseAt: 30 + releaseAfterMs), 600);

        Assert.Equal(Short, r.FinalText);
        Assert.Empty(r.StuckModifiers);
    }

    [Fact]
    public void AltHotkey_HoldAndRepeat_NoStuckAlt()
    {
        var keys = new[] { 30, 300 }.SelectMany(t => new[] { Down(t, L), Up(t + 30, L) });
        var r = RunWithApp(Hold([Ctrl, Alt], keys, releaseAt: 600), 800);

        Assert.Equal("ALTALT", r.FinalText);
        Assert.Empty(r.StuckModifiers);
    }

    private Result RunWithApp(IEnumerable<Step> steps, int observeMs)
    {
        using var sandbox = new InstallSandbox();
        sandbox.Install(builds.NewZip);
        sandbox.WriteSettings("{\"AutoUpdate\":false}");
        File.WriteAllText(Path.Combine(sandbox.DataDir, "hotkeys.json"),
            $$"""[{"Hotkey":"Ctrl+Shift+K","Text":"{{Short}}"},{"Hotkey":"Ctrl+Shift+J","Text":"{{Long}}"},{"Hotkey":"Ctrl+Alt+L","Text":"ALT"}]""");

        using var app = sandbox.LaunchApp();
        Activate(app);

        var result = KeyboardRig.Run(steps, observeMs);
        output.WriteLine(result.ToString());
        output.WriteLine("---- app.log ----\n" + sandbox.ReadAppLog());
        return result;
    }

    // 메인 창의 '활성화' 토글을 켠다(켜야 단축키가 등록된다).
    private static void Activate(Process app)
    {
        using var automation = new UIA3Automation();
        var fa = FlaUI.Core.Application.Attach(app);
        ToggleButton? toggle = null;
        Assert.True(InstallSandbox.WaitUntil(() =>
        {
            toggle = fa.GetAllTopLevelWindows(automation)
                .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId("ActivateToggle")))
                .FirstOrDefault(e => e != null)?.AsToggleButton();
            return toggle != null;
        }, Ui.Timeout, 500), "활성화 토글을 찾지 못함");
        if (toggle!.ToggleState != FlaUI.Core.Definitions.ToggleState.On) toggle.Toggle();
        Assert.True(InstallSandbox.WaitUntil(() => toggle.ToggleState == FlaUI.Core.Definitions.ToggleState.On, TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
    }
}
