namespace AgentRuntime.Core.Worker;

/// <summary>
/// **回执四字段的唯一形状声明处**（结论 / 证据 / 改动 / 未决）。
///
/// <para><b>为什么要有它</b>：2026-09-29 完整验证现场（E739/E744）—— master 可见的那一行是
/// <c>changes="none" open="none"</c>（`key=value`，渲染侧写的），而终局自验闸门按 <c>key:</c> 找字段，
/// 于是**明明有四字段、闸门却说没有** ⇒ <c>[DONE]</c> 被判「归属不明」，永远过不了。
/// 两处各写一套形状 ⇒ 必然分叉（坑 #144 同族）。</para>
///
/// <para><b>口径只有一处</b>：字段名在本清单；**两种写法都认**（<c>key: value</c> 是 worker 的块、
/// <c>key="value"</c> 是 master 可见的渲染行）、大小写不敏感、不要求行首。</para>
/// </summary>
public static class WorkerReceiptShape
{
    /// <summary>契约要求的四字段（顺序即协议里写的顺序）。**旧形状的兼容口径**（2026-09-30 起改用 <see cref="ReceiptKinds"/> 的按类字段）。</summary>
    public static readonly string[] RequiredFields = ["conclusion", "evidence", "changes", "open"];

    /// <summary>回执类的**唯一形状声明处**：枚举名 = <c>kind=</c> 那一格的值（见 <see cref="ReceiptKinds"/>）。</summary>
    public const string KindField = "kind";

    /// <summary>读回执里的 <c>kind</c>（无/不在 6 类里 ⇒ 空）。
    /// <para><b>XML 优先</b>（主人 2026-10-07）：回执若是 <c>&lt;receipt&gt;</c> 元素，就读它的 <c>&lt;kind&gt;</c> 成员；
    /// 否则走旧的 <c>kind=</c> 文本扫描（迁移期的归一后路）。</para></summary>
    public static ReceiptKind? KindOf(string? text) =>
        ReceiptXml.Take(text) is { } xml ? ReceiptKinds.Parse(xml.Kind)
        : text is null ? null : ReceiptKinds.Parse(Value(text, KindField));

    /// <summary>**该类的必需字段里，缺哪一个**（返回 null = 齐）。字段名去掉尾部 <c>=</c> 再找。</summary>
    /// <para>为什么按类取：6 类各有各的必需字段集（<c>done</c> 要 <c>basis</c>、<c>partial</c> 要
    /// <c>covered+missing</c>、<c>blocked</c> 要 <c>blocker+tried</c>…）—— 终局自验只要看**这一类**那几格在不在，
    /// 不再要求“四字段齐”（那正是“失败回执永远活不过闸门”的根）。</para>
    public static string? MissingKindField(string? text, ReceiptKind kind, DispatchIntent? intent = null)
    {
        foreach (var f in ReceiptKinds.RequiredFields(kind, intent))
        {
            var name = f.TrimEnd('=');
            if (!Has(text, name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>**该类禁止出现的字段里，哪一个出现了**（返回 null = 没有）。</summary>
    public static string? PresentForbiddenField(string? text, ReceiptKind kind)
    {
        foreach (var f in ReceiptKinds.ForbiddenFields(kind))
        {
            var name = f.TrimEnd('=');
            if (Has(text, name) && !IsEmpty(Value(text, name)))
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsEmpty(string? v)
    {
        var t = (v ?? string.Empty).Trim().Trim('"').Trim();
        return t.Length == 0 || t.Equals("none", System.StringComparison.OrdinalIgnoreCase) || t is "无" or "-";
    }

    /// <summary>这一份文本里四字段齐吗（两种写法都认）。</summary>
    public static bool HasAllRequired(string? text)
    {
        foreach (var f in RequiredFields)
        {
            if (!Has(text, f))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>这一个字段出现过吗（XML 优先：看成员在不在；否则 `key:` / `key=` 跨行找）。</summary>
    public static bool Has(string? text, string field) =>
        ReceiptXml.Take(text) is { } xml ? xml.Has(field) : HasLegacy(text, field);

    /// <summary>旧路：`key:` / `key=` 跨行找（迁移期保留）。</summary>
    private static bool HasLegacy(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > field.Length
                && t.StartsWith(field, System.StringComparison.OrdinalIgnoreCase)
                && (t[field.Length] == ':' || t[field.Length] == '='))
            {
                return true;                                        // 行首写法（worker 的块 / 渲染行以它开头时）
            }

            if (t.Contains(field + ":", System.StringComparison.OrdinalIgnoreCase)
                || t.Contains(field + "=", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;                                        // 同一行里的多字段（渲染行 = answer=… changes=… open=…）
            }
        }

        return false;
    }

    /// <summary>取这一个字段的值（XML 优先：按名取成员；否则认 `key:` / `key=` 两种写法）。</summary>
    public static string Value(string? text, string field) =>
        ReceiptXml.Take(text) is { } xml ? xml.Value(field) : ValueLegacy(text, field);

    /// <summary>旧路：两种写法都认；渲染行的 `"…"` 会被剥掉引号（迁移期保留）。</summary>
    private static string ValueLegacy(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(field))
        {
            return string.Empty;
        }

        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            System.Text.RegularExpressions.Regex.Escape(field) + "\\s*[:=]\\s*(\"[^\"]*\"|[^|\\n]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success)
        {
            return string.Empty;
        }

        var v = m.Groups[1].Value.Trim();
        if (v.StartsWith('"') && v.EndsWith('"') && v.Length >= 2)
        {
            return v.Trim('"').Trim();
        }

        // 未加引号的值：**截到下一个字段**（渲染行里一行多个字段：`open=none covered=7/7` ⇒ 值是 `none`）。
        var next = System.Text.RegularExpressions.Regex.Match(v, @"\s+[A-Za-z_][A-Za-z0-9_]*\s*[:=]");
        return (next.Success ? v[..next.Index] : v).Trim();
    }
}
