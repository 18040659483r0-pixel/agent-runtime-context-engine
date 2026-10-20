using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **派发意图**（封闭 8 类 · 设计稿 <c>docs/DESIGN-INTENT-TYPES.md</c> §三）。
/// <para>主人 2026-09-30 15:34/15:44/15:47 令：委派方法太宽泛 ⇒ 每次派发 = **一句自然语言目的** ＋
/// **一组由远端模型自选的枚举组合**；组合把"要什么"收窄成**无歧义的固定操作**；
/// 意图之间**完全不交叉**，迫使远端只能选其中一种。</para>
/// <para><b>形状由运行时定，值由模型给</b>（§二·原则 3）：本文件的枚举/交付形状是**唯一声明处**。</para>
/// </summary>
public enum DispatchIntentKind
{
    /// <summary>取**计量读数**（交付：<c>addr= bytes= lines= sha256= [at=]</c>）。</summary>
    Read,

    /// <summary>取**逐字原文**（交付：<c>addr= sha256= text=&lt;逐字&gt;</c>）。</summary>
    ReadVerbatim,

    /// <summary>找**符合条件的地址清单**（交付：<c>items=[…] count=N</c>）。</summary>
    Locate,

    /// <summary>核**一个别人声称过的声明**（交付：<c>claim= actual= verdict=pass|fail</c>）。</summary>
    Verify,

    /// <summary>**新建**产物（交付：<c>path= bytes= lines= sha256= state=created|already-present</c>）。</summary>
    Write,

    /// <summary>既有产物**唯一一处**定点替换（交付：<c>path= before_sha= after_sha= line=</c>）。</summary>
    Edit,

    /// <summary>跑命令并**原样**取回效果（交付：<c>command= rc= stdout=</c>）。</summary>
    Exec,

    /// <summary>据**已有原料**下结论（交付：<c>conclusion=&lt;≤N 行&gt; basis=[…]</c>）。</summary>
    Derive,
}

/// <summary>叠加轴（正交 · §三·2）。组合后的语义由「意图 + 轴」唯一确定。</summary>
public sealed record DispatchIntent(
    DispatchIntentKind Kind,
    IReadOnlyList<string> Scope,
    string Criteria,
    string When,
    string SideEffects)
{
    /// <summary>该意图**唯一**的交付形状（书面 · 判定的锚）。</summary>
    public string DeliveryShape => DispatchIntents.DeliveryShape(Kind);

    /// <summary>该意图**默认**的副作用档（<c>READ/READ#verbatim/LOCATE/VERIFY/DERIVE</c>=none）。</summary>
    public static string DefaultSideEffects(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read or DispatchIntentKind.ReadVerbatim or DispatchIntentKind.Locate
            or DispatchIntentKind.Verify or DispatchIntentKind.Derive => "none",
        _ => "local",
    };

    public string Describe() =>
        $"{DispatchIntents.Name(Kind)}(scope={string.Join(",", Scope)}) criteria={Criteria} when={When} side_effects={SideEffects}";
}

/// <summary>
/// 意图的**机械判据唯一处**（判定表 / 排他列 / 归一 / residue）。
/// <para><b>本类的边界（诚实标注）</b>：只做**机械判据**（词表 + 优先序 + 形状），**不做语义裁决**——
/// 目的句与组合不一致时只**记 residue**，绝不替模型决定（§二·原则 6）。</para>
/// </summary>
public static class DispatchIntents
{
    /// <summary>封闭 8 类的**规范名**（协议区/worker 协议区/日志共用一份）。</summary>
    public static string Name(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read => "READ",
        DispatchIntentKind.ReadVerbatim => "READ#verbatim",
        DispatchIntentKind.Locate => "LOCATE",
        DispatchIntentKind.Verify => "VERIFY",
        DispatchIntentKind.Write => "WRITE",
        DispatchIntentKind.Edit => "EDIT",
        DispatchIntentKind.Exec => "EXEC",
        DispatchIntentKind.Derive => "DERIVE",
        _ => "?",
    };

    /// <summary>每类的**唯一交付形状**（回执必须给出的键；一类一套，互不重叠）。</summary>
    public static string DeliveryShape(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read => "addr= bytes= lines= sha256=",
        DispatchIntentKind.ReadVerbatim => "addr= sha256= text=<verbatim>",
        DispatchIntentKind.Locate => "items=[addr…] count=N",
        DispatchIntentKind.Verify => "claim= actual= verdict=pass|fail",
        DispatchIntentKind.Write => "path= bytes= lines= sha256= state=created|already-present",
        DispatchIntentKind.Edit => "path= before_sha= after_sha= line=",
        DispatchIntentKind.Exec => "command= rc= stdout=",
        DispatchIntentKind.Derive => "conclusion=<≤N lines> basis=[addr…]",
        _ => "?",
    };

    /// <summary>
    /// 该类意图的**含义**（类型层：它是什么 / 什么时候该用它）—— 主人 2026-09-30 17:08 令：
    /// "每一个枚举都要把含义告诉协议区，而不是只写一种典型用法；参考 **类型 ↔ 实例**"。
    /// <para>与 <see cref="DeliveryShape"/> 的分工：含义说"什么时候用"，形状说"交出来长什么样"。</para></summary>
    public static string Meaning(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read => "gimme the MEASUREMENTS of these addresses -- numbers and identity (bytes, lines, sha256), not the text; use it whenever you need to know size or to pin a file's identity",
        DispatchIntentKind.ReadVerbatim => "gimme the TEXT ITSELF, verbatim -- use it only when the exact characters matter (quoting a line, checking wording); never as a substitute for measurements",
        DispatchIntentKind.Locate => "gimme the ADDRESSES that match a feature (which files contain X / what looks suspicious) -- the answer is a list of addresses plus a count, never the content",
        DispatchIntentKind.Verify => "CHECK A CLAIM someone already made (a sha, a count, a line) against what is on disk -- it needs the claim to check, otherwise it is just READ",
        DispatchIntentKind.Write => "CREATE an artifact that does not exist yet -- brand new file/dir; if the target already exists and must change, that is EDIT",
        DispatchIntentKind.Edit => "CHANGE one existing artifact in exactly one place (one replacement) -- never a whole-file rewrite (that would be WRITE) and never several places at once (send several EDITs)",
        DispatchIntentKind.Exec => "RUN a command and hand back its RAW effect (command, exit code, stdout as-is) -- use it when the answer is not expressible as measurements; if it is, READ is narrower and cheaper",
        DispatchIntentKind.Derive => "REACH A CONCLUSION from material you already hold (nothing new is read from disk, nothing is written) -- the answer is a conclusion plus the addresses it rests on",
        _ => "",
    };

    /// <summary>该类意图的**实例**（实例层：一个真例），与 <see cref="Meaning"/> 配成「类型 ↔ 实例」。</summary>
    public static string Instance(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read => "READ(scope=/abs/f.txt) criteria=full  ->  addr=/abs/f.txt bytes=135 lines=2 sha256=5534879…",
        DispatchIntentKind.ReadVerbatim => "READ#verbatim(scope=/abs/f.txt) criteria=lines 40-41  ->  addr=/abs/f.txt sha256=5534879… text=\"…two exact lines…\"",
        DispatchIntentKind.Locate => "LOCATE(scope=/abs/repo) criteria=full  ->  items=[/abs/repo/a.md /abs/repo/b/c.cs] count=2",
        DispatchIntentKind.Verify => "VERIFY(scope=/abs/f.txt) criteria=full  ->  claim=sha256:5534879… actual=sha256:5534879… verdict=pass",
        DispatchIntentKind.Write => "WRITE(scope=/abs/new/dir) criteria=full  ->  path=/abs/new/dir/verify.txt bytes=34 lines=3 sha256=033c43… state=created",
        DispatchIntentKind.Edit => "EDIT(scope=/abs/f.txt) criteria=full  ->  path=/abs/f.txt before_sha=033c43… after_sha=7a3ffd… line=settled",
        DispatchIntentKind.Exec => "EXEC(scope=/abs/f.txt) criteria=full  ->  command=shasum -a 256 /abs/f.txt rc=0 stdout=5534879… /abs/f.txt",
        DispatchIntentKind.Derive => "DERIVE(scope=/abs/f.txt) criteria=full  ->  conclusion=<3 lines> basis=[/abs/f.txt:40-41 /abs/g.md:7]",
        _ => "",
    };

    /// <summary>
    /// 该类意图回执**必须给出**的键（**唯一声明处**；与 <see cref="DeliveryShape"/> 由夹具钉住一致）。
    /// <para>主人 2026-09-30 16:44 口径：交付形状由**意图唯一决定**，worker **无裁量**；缺键 = 不合格 ⇒ 退回，
    /// 运行时**不兜底、不宽容**（"不需要某个真值"不是省略的理由 —— 那是选错了意图 / criteria）。</para></summary>
    /// <summary>
    /// **窗口规则（唯一声明处 · 协议区 <c>[WINDOW]</c> 由它渲染）**：窗口是**读源**还是**写目标** ——
    /// 读源必须**已存在**；写目标只要**它所在的那一层**存在（新建文件是常态）。
    /// <para>运行时只做**机械查表 + 存在性检查**（存在性它自己就能测）；"读/写"这个**规则**写在协议区，远端据此自律。</para>
    /// </summary>
    public static bool WindowMustExist(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read or DispatchIntentKind.ReadVerbatim
            or DispatchIntentKind.Locate or DispatchIntentKind.Verify or DispatchIntentKind.Derive => true,
        _ => false,
    };

    /// <summary>**跨树守卫只对写侧生效**（2026-10-09 主人令「B」）：WRITE/EDIT 的**写目标**靠 base 解析（跨树会「读得到、写不对」）；
    /// 读类 / EXEC 的 window 只是「读源 / 范围提示」⇒ 跨树无害（放行 + 注记）。</summary>
    public static bool WindowMustStayInBase(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Write or DispatchIntentKind.Edit => true,
        _ => false,
    };

    public static IReadOnlyList<string> DeliveryKeys(DispatchIntentKind kind) => kind switch
    {
        DispatchIntentKind.Read => ["addr=", "bytes=", "lines=", "sha256="],
        DispatchIntentKind.ReadVerbatim => ["addr=", "sha256=", "text="],
        DispatchIntentKind.Locate => ["items=", "count="],
        DispatchIntentKind.Verify => ["claim=", "actual=", "verdict="],
        DispatchIntentKind.Write => ["path=", "bytes=", "lines=", "sha256=", "state="],
        DispatchIntentKind.Edit => ["path=", "before_sha=", "after_sha=", "line="],
        DispatchIntentKind.Exec => ["command=", "rc=", "stdout="],
        DispatchIntentKind.Derive => ["conclusion=", "basis="],
        _ => [],
    };

    /// <summary>`criteria` 的**封闭取值**（另有 <c>free "…"</c> 一档：不计入机器判定，原文进 ASK）。</summary>
    public static readonly string[] CriteriaKinds = ["full", "lines", "count", "members", "free"];

    /// <summary>不接受的组合（**必须红** · §三·3）。返回 null = 合法。</summary>
    public static string? IllegalCombination(DispatchIntent i)
    {
        if (i.Kind == DispatchIntentKind.Locate && i.Criteria.StartsWith("lines", StringComparison.OrdinalIgnoreCase))
            return "LOCATE 的清单没有行区间 ⇒ criteria 不能用 lines";
        if (i.Kind == DispatchIntentKind.Write && i.Criteria.StartsWith("lines", StringComparison.OrdinalIgnoreCase))
            return "WRITE 是整份新建、不是读窗口 ⇒ criteria 不能用 lines";
        if (i.Kind is DispatchIntentKind.Read or DispatchIntentKind.ReadVerbatim && i.Criteria.StartsWith("members", StringComparison.OrdinalIgnoreCase))
            return "读类没有「集合成员」判据 ⇒ criteria 不能用 members";
        if (i.Kind == DispatchIntentKind.Verify && i.Criteria.StartsWith("count", StringComparison.OrdinalIgnoreCase))
            return "VERIFY 核的是一个声明，不是计数 ⇒ criteria 不能用 count";
        var want = DispatchIntent.DefaultSideEffects(i.Kind);
        if (!string.Equals(i.SideEffects, want, StringComparison.OrdinalIgnoreCase))
            return $"side_effects 与意图不符：{Name(i.Kind)} 应为 {want}，给了 {i.SideEffects}";
        return null;
    }

    // ---------------------------------------------------------------- 词表（机械判据）
    public static DispatchIntent? Parse(string? combination, IReadOnlyList<string>? scope = null)
    {
        var t = (combination ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var kind = ParseKind(t);
        if (kind is null)
        {
            return null;
        }

        var axes = t[(t.IndexOf('(') >= 0 ? t.IndexOf('(') : 0)..];
        var sc = scope is { Count: > 0 } ? scope : Arg(axes, "scope") is { Length: > 0 } s
            ? s.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var criteria = Arg(axes, "criteria") is { Length: > 0 } c ? c : "full";
        var when = Arg(axes, "when") is { Length: > 0 } w ? w : "sync";
        var fx = Arg(axes, "side_effects") is { Length: > 0 } f ? f : DispatchIntent.DefaultSideEffects(kind.Value);

        return new DispatchIntent(kind.Value, sc, criteria, when, fx);
    }

    /// <summary>类名 ⇒ 枚举（大小写不敏感；认 <c>READ#verbatim</c> 与 <c>read_verbatim</c>）。</summary>
    public static DispatchIntentKind? ParseKind(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        var cut = t.IndexOfAny(['(', ' ', '|']);
        var head = (cut > 0 ? t[..cut] : t).Replace("_", "#").Trim();
        foreach (var kind in Enum.GetValues<DispatchIntentKind>())
        {
            if (string.Equals(Name(kind), head, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>轴值：<c>key=value</c>（到空格 / <c>)</c> 为止）。</summary>
    private static string Arg(string axes, string key)
    {
        var at = axes.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return string.Empty;
        }

        var s = axes[(at + key.Length + 1)..];
        var end = s.IndexOfAny([' ', ')', '|']);
        return (end >= 0 ? s[..end] : s).Trim().Trim('"');
    }
}
