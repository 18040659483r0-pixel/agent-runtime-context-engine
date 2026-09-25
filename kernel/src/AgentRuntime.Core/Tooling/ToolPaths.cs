using System.Runtime.InteropServices;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **路径规范化（S1）** —— 工具面**不做路径围栏**（主人 2026-09-16 03:00 定：目标就是「能改本机任何文件」），
/// 于是安全性只能落在「审批人看到的是不是**真身**」上（<c>docs/DESIGN-TOOL-FACE.md</c> §四·二 S1）。
/// <para>
/// 本类只做一件事：把模型给的**任意写法**变成一个**绝对的、解析过符号链接的**规范路径。
/// </para>
/// <list type="number">
/// <item><c>~</c> / <c>~/…</c> ⇒ 用户主目录（<c>~user</c> 判不出 ⇒ 拒绝，不猜）；</item>
/// <item>相对路径 ⇒ 以**当前工作目录**为基准（模型不写绝对路径时，它心里想的就是 cwd）；</item>
/// <item><c>.</c> / <c>..</c> / 重复分隔符 ⇒ 由 <see cref="Path.GetFullPath(string)"/> 归一；</item>
/// <item><b>符号链接 ⇒ 解析到最终目标</b>（<c>realpath(3)</c>，含中间目录的链接）——
/// 否则同一份文件会以 <c>/tmp/x</c> 与 <c>/private/tmp/x</c> 两个"不同的路径"出现在审批面上，
/// 而那正是审批欺骗想要的效果。</item>
/// </list>
/// <para>
/// ⚠️ 规范化失败（空 / 只有空白 / <c>~user</c>）**一律抛 <see cref="ToolUsageException"/>** ——
/// 判不出真身 ⇒ **拒绝**，绝不"尽力而为地猜一个路径"（fail-closed，S5）。
/// </para>
/// <para>
/// 判据只有一条，因此工具（<c>read</c>/<c>list</c>/<c>write</c>/<c>edit</c>）与审批面**共用本类**：
/// 审批面上写的路径与真要改的那个文件**由同一段代码算出**（不是"两份实现碰巧一致"）。
/// </para>
/// </summary>
public static class ToolPaths
{
    /// <summary>realpath 的缓冲区（macOS <c>PATH_MAX</c>=1024，Linux=4096；不够时 realpath 会失败 ⇒ 退回已归一路径）。</summary>
    private const int RealPathBufferSize = 4096;

    /// <summary>把路径变成**规范绝对路径（真身）**。空 / 判不出 ⇒ 抛 <see cref="ToolUsageException"/>。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolUsageException("路径为空（协议要求显式给 path）。");
        }

        var expanded = ExpandHome(path.Trim());
        var full = Path.GetFullPath(expanded);       // 相对 ⇒ cwd；吃掉 . / ..
        return TrimTrailingSeparator(ResolveLinks(full));
    }

    /// <summary>
    /// 规范化的**安全版**：判不出真身 ⇒ **原样返回**（不抛）。
    /// <para>用在「集合成员比对」这类场景（污染集 / 预授权目标）：① 在那里抛异常会把
    /// 「这个路径可疑」变成「整个判定炸掉」；② 同一份文件的两种拼法（macOS <c>/var</c> ↔
    /// <c>/private/var</c>）必须收敛到**同一条真身** —— 否则字符串相等**恒假**、集合**静默不命中**
    /// （PITFALLS #139 / <c>§十·54</c>）。</para>
    /// </summary>
    public static string NormalizeOrSelf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        try
        {
            return Normalize(path);
        }
        catch (ToolUsageException)
        {
            return path;
        }
    }

    /// <summary>
    /// 参数里 <c>path</c> 字段的规范化结果（**一次一批的摘要原料**，S4）：没有该字段 ⇒ 空串。
    /// <para>与审批面用的是同一条口径 ⇒ 人点的那个动作 = 摘要绑定的那个动作。</para>
    /// </summary>
    public static string NormalizePathArgument(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var path = args.OptionalString("path");
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Normalize(path);
    }

    /// <summary>主目录（判不出 ⇒ 抛：宁可拒绝，也不要拼出一个猜出来的路径）。</summary>
    public static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new ToolUsageException("判不出用户主目录（~ 无法展开）⇒ 拒绝；请给绝对路径。");
        }

        return home;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return HomeDirectory();
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(HomeDirectory(), path[2..]);
        }

        if (path.StartsWith('~'))
        {
            throw new ToolUsageException(
                $"路径 \"{path}\" 用不支持的写法：只解析 ~ 与 ~/（~user 判不出主目录）⇒ 拒绝，请给绝对路径。");
        }

        return path;
    }

    /// <summary>
    /// 解析符号链接：找出**最长的已存在前缀**，对它做 <c>realpath</c>，再把剩下的（尚不存在的）尾巴接回去。
    /// <para>为什么要这样绕：<c>realpath</c> 对不存在的路径会失败，而 <c>write</c> 的目标经常还不存在。</para>
    /// </summary>
    private static string ResolveLinks(string full)
    {
        var (current, tail) = SplitAtExistingPrefix(TrimTrailingSeparator(full));

        var resolved = TryRealPath(current) ?? current;
        for (var i = tail.Count - 1; i >= 0; i--)
        {
            resolved = Path.Combine(resolved, tail[i]);
        }

        return Path.GetFullPath(resolved);
    }

    private static (string Existing, List<string> Tail) SplitAtExistingPrefix(string full)
    {
        var tail = new List<string>();
        var current = full;

        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return (full, []);   // 一路到根都不存在：原样交给打开动作去报错
            }

            var name = Path.GetFileName(current);
            if (name.Length == 0)
            {
                return (full, []);
            }

            tail.Add(name);
            current = parent;
        }

        return (current, tail);
    }

    /// <summary>
    /// <c>realpath(3)</c>（解析全部符号链接，含中间目录）。
    /// <para>Windows 走 <c>Path.GetFullPath</c> 的归一结果（不解析链接）—— 本版工具面在 Windows 侧只要求"绝对 + 归一"。</para>
    /// </summary>
    private static string? TryRealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return path;
        }

        var buffer = Marshal.AllocHGlobal(RealPathBufferSize);
        try
        {
            return realpath(path, buffer) == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        if (path.Length <= root.Length)
        {
            return path;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? root : trimmed;
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);
}
