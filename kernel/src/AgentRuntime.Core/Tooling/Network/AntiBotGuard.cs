namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// **反爬页识别**（2026-10-27 真机）：百度对非浏览器客户端一律 302 到图形验证码页，
/// 而旧工具把 `status=200` 当成功交回 ⇒ 模型拿到的「内容」其实是验证码页。
/// <para>
/// 判据只认**强特征**，避免误伤正常页面：
/// ① 最终地址里含 `captcha`；② **篇幅极短**（≤8000 字符）且正文含验证词。
/// </para>
/// </summary>
public static class AntiBotGuard
{
    /// <summary>页面正文里的强特征词（只在小页面上才采信）。</summary>
    private static readonly string[] Markers =
    [
        "安全验证", "人机验证", "滑动验证", "请输入验证码", "unusual traffic", "captcha",
    ];

    /// <summary>命中时的原因（null = 不像反爬页）。</summary>
    public static string? Detect(NetworkResponse response)
    {
        if (response.Status is < 200 or >= 300)
        {
            return null;
        }

        if (response.FinalUrl.Contains("captcha", StringComparison.OrdinalIgnoreCase))
        {
            return "最终地址是验证码页";
        }

        if (response.Body.Length > 8000)
        {
            return null;
        }

        foreach (var marker in Markers)
        {
            if (response.Body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return $"正文含「{marker}」且篇幅极短（{response.Body.Length} 字符）";
            }
        }

        return null;
    }
}
