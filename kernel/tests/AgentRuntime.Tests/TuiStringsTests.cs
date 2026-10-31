using AgentRuntime.Presentation;
using AgentRuntime.Tui2;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>TUI2 语言包（2026-10-29）：**默认英文**，另有中文 / 日本語；三语键集必须一一对应。</summary>
public sealed class TuiStringsTests
{
    [Fact]
    public void 默认是英文()
    {
        Assert.Equal(TuiLanguage.En, TuiStrings.Language == TuiLanguage.Zh || TuiStrings.Language == TuiLanguage.Ja
            ? TuiStrings.Language
            : TuiLanguage.En);
        Assert.Equal("Session", TuiStrings.T(TuiKey.Session));
        Assert.Equal("Stack", TuiStrings.T(TuiKey.Stack));
        Assert.Equal("cache hit 95%", TuiStrings.F(TuiKey.CacheHit, "95%"));
    }

    [Fact]
    public void 三语都能解析_且不落回键名()
    {
        var original = TuiStrings.Language;
        try
        {
            foreach (var language in new[] { TuiLanguage.En, TuiLanguage.Zh, TuiLanguage.Ja })
            {
                TuiStrings.Language = language;
                foreach (var key in TuiStrings.Keys)
                {
                    var text = TuiStrings.T(key);
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{language}/{key} 为空");
                }
            }

            TuiStrings.Language = TuiLanguage.Zh;
            Assert.Equal("会话", TuiStrings.T(TuiKey.Session));
            TuiStrings.Language = TuiLanguage.Ja;
            Assert.Equal("セッション", TuiStrings.T(TuiKey.Session));
        }
        finally
        {
            TuiStrings.Language = original;
        }
    }

    [Fact]
    public void 英文档不含汉字_且启动播报走语言包()
    {
        var original = TuiStrings.Language;
        try
        {
            TuiStrings.Language = TuiLanguage.En;
            foreach (var key in TuiStrings.Keys)
            {
                var text = TuiStrings.T(key);
                Assert.DoesNotContain(text, c => c >= '\u4e00' && c <= '\u9fff');
            }

            // 启动播报（SessionLifecycle.StartReport）必须走语言包：英文档下不得出现汉字。
            var report = new AgentRuntime.Hosting.StartReport(1, "/tmp/stream.xml", null, 0, 0, null);
            foreach (var line in report.Lines())
            {
                Assert.DoesNotContain(line, c => c >= '\u4e00' && c <= '\u9fff');
            }

            // 切到中文后同一份文案必须是中文（证明不是「只剩英文」）：
            TuiStrings.Language = TuiLanguage.Zh;
            Assert.Contains(report.Lines(), line => line.Contains("[开始]", StringComparison.Ordinal));
        }
        finally
        {
            TuiStrings.Language = original;
        }
    }

    [Fact]
    public void 带参数的三语都成型()
    {
        var original = TuiStrings.Language;
        try
        {
            TuiStrings.Language = TuiLanguage.En;
            Assert.Equal("9 modules", TuiStrings.F(TuiKey.Modules, 9));
            TuiStrings.Language = TuiLanguage.Zh;
            Assert.Equal("9 模块", TuiStrings.F(TuiKey.Modules, 9));
            TuiStrings.Language = TuiLanguage.Ja;
            Assert.Equal("9 モジュール", TuiStrings.F(TuiKey.Modules, 9));
        }
        finally
        {
            TuiStrings.Language = original;
        }
    }

    [Fact]
    public void 错误行前缀按当前语言归一()
    {
        // 真机（2026-10-31）：屏上出现 `[错误] the path is empty.` —— 中文前缀混在英文句子里。
        var original = TuiStrings.Language;
        try
        {
            TuiStrings.Language = TuiLanguage.En;
            Assert.Equal("[error] the path is empty.", Tui2Style.ErrorLine("[错误] the path is empty."));
            Assert.Equal("[error] the path is empty.", Tui2Style.ErrorLine("[error] the path is empty."));

            TuiStrings.Language = TuiLanguage.Zh;
            Assert.Equal("[错误] the path is empty.", Tui2Style.ErrorLine("[error] the path is empty."));

            TuiStrings.Language = TuiLanguage.Ja;
            Assert.Equal("[エラー] x", Tui2Style.ErrorLine("[错误] x"));
        }
        finally
        {
            TuiStrings.Language = original;
        }
    }
}
