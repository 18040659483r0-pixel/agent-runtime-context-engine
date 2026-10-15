using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **截断时的「真指针」**（C1，2026-09-26）—— 工具结果被上限截断时，把**完整正文**落到
/// <see cref="ToolLimits.ResultDirectory"/>，并把**可走通的路径**写进话术。
/// <para>
/// 为什么需要：原来的话术写「全文在 <c>/trace</c>」，那是个**假指针** —— <c>/trace</c> 是 TUI 看
/// 生命周期轨迹的命令，**不是**这条工具结果的原文落点（原文在写入那一刻就被截掉了，流里根本没有全文）。
/// 判据只有一个：**顺着指针一步能不能拿到全文**；指不到，省下的字节就会变成多出来的轮数
/// （一轮 ≈ 一次整份上下文重发）。
/// </para>
/// <para>文件名 = 正文 sha256 前 16 位 ⇒ 确定性、可复算、幂等（同一份正文不重写）。</para>
/// </summary>
internal static class TruncationPointer
{
    /// <summary>正文 sha256（小写十六进制）。</summary>
    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>前 12 位（话术里用）。</summary>
    public static string ShortSha(string text) => Sha256(text)[..12];

    /// <summary>
    /// 把完整正文落盘并返回**指针话术**。本方法**自己绝不抛**（诊断器不能变成第二个崩溃源）：
    /// 落盘失败只退化成一句实话，绝不让「截断话术」把结果搞崩。
    /// </summary>
    public static string Of(string fullText, ToolLimits limits, int maxChars)
    {
        var head = $"\n…（按字符上限截断：只给前 {maxChars} 字符，本条共 {fullText.Length} 字符";
        var landing = TryLand(fullText, limits);
        return landing is null
            ? head + "；**完整正文没落盘**（落点写失败）—— 要另一段就缩小窗口 / 把命令收窄）"
            : head + $" ⇒ 全文 {landing}；用 read 读它（或缩小窗口））";
    }

    private static string? TryLand(string fullText, ToolLimits limits)
    {
        try
        {
            Directory.CreateDirectory(limits.ResultDirectory);
            var target = Path.Combine(limits.ResultDirectory, Sha256(fullText)[..16] + ".txt");
            if (!File.Exists(target))
            {
                File.WriteAllText(target, fullText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
