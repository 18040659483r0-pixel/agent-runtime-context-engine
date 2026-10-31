using AgentRuntime.Tui2;
using Terminal.Gui.Editor.Document;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// TUI2 键位（2026-10-31 主人令）：**Esc 一下 = 暂停远端 AI；Esc 两下 = 退出**；Ctrl+C 不再退出。
/// <para>判据都在控制器上：手势时间窗与「哪些键会退出」是同一处声明，避免两处各判一次而分叉。</para>
/// </summary>
public sealed class Tui2KeybindingTests
{
    private static InputController New() => new(new EditorBridge(new TextDocument()), new PasteModel());

    [Fact]
    public void Esc一下不退出_两下才退出()
    {
        var ctrl = New();

        Assert.False(ctrl.EscQuitGesture(), "第一下 Esc 只算「暂停」");
        Assert.True(ctrl.EscQuitGesture(), "2 秒内第二下 Esc ⇒ 退出");
    }

    [Fact]
    public void 三下Esc只退出一次_状态被复位()
    {
        var ctrl = New();

        Assert.False(ctrl.EscQuitGesture());
        Assert.True(ctrl.EscQuitGesture());      // 退出手势用掉
        Assert.False(ctrl.EscQuitGesture());     // 复位后重新计数
    }

    [Fact]
    public void CtrlC不再是退出键()
    {
        var ctrl = New();

        var action = ctrl.Handle(new KeyEvent(InputKey.CtrlC), out _);

        Assert.NotEqual(InputAction.Quit, action);
    }

    [Fact]
    public void Esc键走手势_第二下返回Quit()
    {
        var ctrl = New();

        Assert.NotEqual(InputAction.Quit, ctrl.Handle(new KeyEvent(InputKey.Escape), out _));
        Assert.Equal(InputAction.Quit, ctrl.Handle(new KeyEvent(InputKey.Escape), out _));
    }
}
