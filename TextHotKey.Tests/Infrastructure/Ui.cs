using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace TextHotKey.Tests.Infrastructure;

// 실행 중인 앱의 창을 UI 자동화(FlaUI)로 조작한다.
internal static class Ui
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // 프로세스의 창들에서 이름이 name인 버튼을 찾아 누른다.
    public static bool ClickButton(Process process, string name)
    {
        using var automation = new UIA3Automation();
        var app = FlaUI.Core.Application.Attach(process);
        return InstallSandbox.WaitUntil(() =>
        {
            foreach (var w in app.GetAllTopLevelWindows(automation))
            {
                var btn = w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)));
                if (btn != null)
                {
                    btn.AsButton().Invoke();
                    return true;
                }
            }
            return false;
        }, Timeout, 500);
    }

    public static bool WaitForText(Process process, string text)
    {
        using var automation = new UIA3Automation();
        var app = FlaUI.Core.Application.Attach(process);
        return InstallSandbox.WaitUntil(() =>
            app.GetAllTopLevelWindows(automation).Any(w =>
                w.FindAllDescendants().Any(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(text))),
            Timeout, 500);
    }
}
