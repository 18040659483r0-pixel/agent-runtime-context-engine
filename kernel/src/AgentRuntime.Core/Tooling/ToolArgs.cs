using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **一次工具调用的参数**（JSON-in-text 的**受控**解析面）。
/// <para>
/// 协议写的是 <c>[TOOL] name {"key":"value"}</c>（v18 起形状写死），即「JSON 藏在文本里」⇒ 解析必须**严谨**（本来就是文本协议的主要风险）：
/// </para>
/// <list type="number">
/// <item>必须是 **JSON 对象**（数组 / 字符串 / 数字一律拒）；</item>
/// <item><b>重复键</b>拒（<c>{"path":"a","path":"b"}</c> 到底用哪个？判不出 ⇒ fail-closed）；</item>
/// <item><b>未声明的键</b>拒（<c>pth</c> 打错一个字若被静默忽略，就会「什么都没发生」地跑掉）；</item>
/// <item>类型必须对得上（字符串字段给数字 ⇒ 拒；<c>maxLines</c> 给 <c>"200"</c> 这种数字串 ⇒ 容忍，
/// 因为模型被 JSON 转义坑到过很多次，而它**语义无歧义**）。</item>
/// </list>
/// <para>
/// 另外给一个 <see cref="Digest"/>：账本用来记「批的是**哪一个**具体动作」（一次一批的键）。
/// 账本字段**不进 prompt**。
/// </para>
/// </summary>
public sealed class ToolArgs
{
    private readonly Dictionary<string, JsonElement> _values;

    private ToolArgs(string raw, string canonical, Dictionary<string, JsonElement> values)
    {
        Raw = raw;
        Canonical = canonical;
        _values = values;
    }

    /// <summary>原样参数文本（模型写的那一份；报错话术里回显用）。</summary>
    public string Raw { get; }

    /// <summary>规范化 JSON（键按序排、无多余空白）—— 账本与摘要的唯一口径。</summary>
    public string Canonical { get; }

    /// <summary>参数名清单（已按序）。</summary>
    public IReadOnlyCollection<string> Names => _values.Keys;

    /// <summary>
    /// 解析参数文本。<b>任何不合语法 ⇒ 抛 <see cref="ToolUsageException"/></b>（由调用方转成「拒绝」）。
    /// </summary>
    public static ToolArgs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ToolUsageException("工具参数为空：形状 `[TOOL] name {\"key\":\"value\"}`（例如 read {\"path\":\"a.txt\"}）。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // 2026-09-22（坑 #130，B 案）：JSON 拒了之后要能**照着改** ——
            // 真机反复犯的是「多行正文塞进 JSON 字符串」，机器腔的 JsonException 说不清这一点。
            // 2026-09-24（坑 #157，**同族第三个病因**）：模型把 shell 正则原样搬进 JSON 字符串
            //（`grep -n 'a\|b'`）⇒ 反斜杠 + `|` / `(` 不是合法 JSON 转义 ⇒ **整批工具调用被拒**。
            throw new ToolUsageException($"工具参数不是合法 JSON：{ex.Message}{JsonShapeHint(json)}（原文：{Truncate(json)}）");
        }

        // 局部函数：**只在字符串内部**看形状（对象字段之间的换行是合法 JSON，不能当病因）。
        // 病因一（坑 #130）：串里有真换行；病因二（坑 #157）：串里有**非法的反斜杠转义**。
        // 两条都必须**点名那一条** + 给一条可执行的修法 —— 否则模型按旧话术去查引号，越查越偏
        //（话术只说一个病因时，换一个病因就等于没话术）。
        static string JsonShapeHint(string json)
        {
            var inString = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (!inString)
                {
                    if (c == '"') inString = true;
                    continue;
                }

                if (c == '\\')
                {
                    var next = i + 1 < json.Length ? json[i + 1] : '\0';
                    if (!IsJsonEscape(next))
                    {
                        var what = next == '\0' ? "反斜杠落在串尾" : $"反斜杠 + {next}";
                        return $" —— JSON 的字符串里出现了**非法的转义**（{what}）："
                             + "JSON 只认 \\\" \\\\ \\/ \\b \\f \\n \\r \\t \\uXXXX，其余一律非法；"
                             + "**正则里的元字符不要加反斜杠** —— 要写 `(` `|` 就直接写，或整条命令用单引号包住（必要时 `grep -F`）。";
                    }

                    i++;    // 合法转义：跳过被转义的那个字符
                    continue;
                }

                if (c == '"') inString = false;
                else if (c == '\n') return " —— 字符串里有**没转义的换行**：多行正文不要塞进单行 JSON（写文件直接用 `edit` 工具）";
            }

            return string.Empty;
        }

        // 合法 JSON 转义 = 引号 / 反斜杠 / 斜杠 / 单字符族 / uXXXX（RFC 8259 §7）。
        static bool IsJsonEscape(char c) => c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u';

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ToolUsageException($"工具参数必须是 JSON 对象，实际是 {root.ValueKind}（原文：{Truncate(json)}）。");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, property.Value.Clone()))
                {
                    throw new ToolUsageException($"工具参数出现重复键 \"{property.Name}\"：判不出该用哪一个 ⇒ 拒绝。");
                }
            }

            return new ToolArgs(json, CanonicalOf(values), values);
        }
    }

    /// <summary>**只允许这些键**（未声明的键 ⇒ 抛错；防「打错一个字就静默什么都没做」）。</summary>
    public ToolArgs EnsureOnly(params string[] allowed)
    {
        foreach (var name in _values.Keys)
        {
            if (!Array.Exists(allowed, a => string.Equals(a, name, StringComparison.Ordinal)))
            {
                throw new ToolUsageException($"工具参数里有未声明的键 \"{name}\"（允许：{string.Join(", ", allowed)}）。");
            }
        }

        return this;
    }

    /// <summary>必填字符串。</summary>
    public string RequireString(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            throw new ToolUsageException($"工具参数缺少必填字段 \"{name}\"。");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolUsageException($"工具参数字段 \"{name}\" 必须是字符串，实际是 {value.ValueKind}。");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>可选字符串（缺省 = null；给了但类型不对 ⇒ 拒）。</summary>
    public string? OptionalString(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolUsageException($"工具参数字段 \"{name}\" 必须是字符串，实际是 {value.ValueKind}。");
        }

        return value.GetString();
    }

    /// <summary>可选整数（数字，或**语义无歧义**的数字串；其余 ⇒ 拒）。</summary>
    public int? OptionalInt(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ToolUsageException($"工具参数字段 \"{name}\" 必须是整数，实际是 {value.ValueKind}。");
    }

    /// <summary>参数摘要（账本「批的是哪一个动作」的键；sha256 前 12 位）。</summary>
    public string Digest(string tool) => ApprovalDigest.Of(tool, Canonical);

    private static string CanonicalOf(Dictionary<string, JsonElement> values)
    {
        var parts = values
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"\"{p.Key}\":{JsonSerializer.Serialize(p.Value)}");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";
}

/// <summary>
/// **审批摘要**（一次一批的键）—— 一行的 sha256 前 12 位。
/// <para>口径唯一：工具名 + 规范化参数。<b>不进 prompt</b>（账本字段）。</para>
/// </summary>
public static class ApprovalDigest
{
    /// <summary>摘要位数（与区栈指纹同口径，方便人眼比对）。</summary>
    public const int DigestChars = 12;

    /// <summary>算摘要（工具名 + 规范化参数；无路径维度的调用点用这个）。</summary>
    public static string Of(string tool, string canonicalArgs) => Of(tool, canonicalArgs, string.Empty);

    /// <summary>
    /// 算摘要（**工具名 + 规范化绝对路径（真身）+ 规范化参数**）—— 一次一批的绑定键（S4）。
    /// <para>
    /// 为什么把路径单列一个维度：审批**不做路径围栏**（可改本机任何文件），所以"点的是哪个文件"
    /// 本身就是动作身份的一部分 —— 路径换了就得重新点头，不能拿旧许可去改另一个文件。
    /// </para>
    /// </summary>
    /// <param name="tool">工具名。</param>
    /// <param name="canonicalArgs">规范化参数（含内容 ⇒ 内容哈希绑定）。</param>
    /// <param name="normalizedPath">规范化绝对路径（真身）；无路径参数的工具传空串。</param>
    public static string Of(string tool, string canonicalArgs, string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(canonicalArgs);
        ArgumentNullException.ThrowIfNull(normalizedPath);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tool}\n{normalizedPath}\n{canonicalArgs}"));
        return Convert.ToHexString(bytes)[..DigestChars].ToLowerInvariant();
    }
}
