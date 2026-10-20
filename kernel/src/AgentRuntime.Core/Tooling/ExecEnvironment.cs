using System.Diagnostics;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// exec 子进程环境（唯一处）：起进程前统一钉住的东西。
/// <para>这里只放<b>与具体命令无关、漏了就会静默出错</b>的项；命令自己的环境由调用者用 shell 语法设。</para>
/// </summary>
public static class ExecEnvironment
{
    /// <summary>macOS 上明确的 UTF-8 locale；Linux 用 <c>C.UTF-8</c>（不依赖装了哪些语言包）。</summary>
    private static string Utf8Locale => OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";

    /// <summary>
    /// 钉 UTF-8 locale。
    /// <para>
    /// <b>为什么必须钉</b>：macOS/Linux 的 svn 客户端在非 UTF-8 locale 下，一遇到非 ASCII 正文
    /// 或提交信息就报 <c>E000022 不能把字符串从 UTF-8 编码转换为本地编码</c> —— 提交直接失败。
    /// 2026-10-18 真机：WB 的每一条中文提交都这样失败，它据此把「客户端 locale 缺失」误判成
    /// 「SVN 服务器写坏了」，换路径、换名字、加字节试了几十轮（现象不冒称机制：locale 才是机制）。
    /// </para>
    /// <para>
    /// 判据：已经显式设成 <b>UTF-8</b> 的调用者保持原值；没设、或设成非 UTF-8 的，
    /// 一律纠正成 UTF-8（那个值只会让提交静默失败，没有任何一条命令需要它）。
    /// </para>
    /// </summary>
    public static void PinUtf8Locale(ProcessStartInfo info)
    {
        var current = info.Environment.TryGetValue("LC_ALL", out var all) && !string.IsNullOrEmpty(all)
            ? all
            : info.Environment.TryGetValue("LANG", out var lang) ? lang : null;

        if (current is not null && current.Contains("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        info.Environment["LC_ALL"] = Utf8Locale;
        info.Environment["LANG"] = Utf8Locale;
    }
}
