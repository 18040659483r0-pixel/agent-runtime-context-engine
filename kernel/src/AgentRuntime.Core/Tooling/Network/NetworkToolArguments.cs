namespace AgentRuntime.Core.Tooling.Network;

/// <summary>通用 HTTP 的方法（封闭集；解析不进 <c>switch(string)</c> 的业务分支）。</summary>
public enum HttpMethodKind
{
    Get,
    Head,
    Post,
    Put,
    Patch,
    Delete,
}

/// <summary><c>web_fetch</c> 的返回形态。</summary>
public enum WebFetchFormat
{
    Text,
    Html,
}

/// <summary>搜索的时间范围（封闭集）。</summary>
public enum SearchRecency
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>下载冲突策略（封闭集；默认 fail = 不覆盖）。</summary>
public enum DownloadConflict
{
    Fail,
    Rename,
    Overwrite,
}

/// <summary>一条请求头（强类型；只允许白名单名字）。</summary>
public sealed record RequestHeader(string Name, string Value);

/// <summary>
/// 网络工具参数的**强类型解析**（唯一入口）。
/// <para>
/// 线上形状仍是 XML 成员（<c>&lt;method&gt;POST&lt;/method&gt;</c>）；这里把字符串一次翻成枚举 / 记录，
/// 之后工具内部不再比较裸字符串 —— 免得「大小写 / 空格 / 同义词」散落成多个坑。
/// </para>
/// </summary>
public static class NetworkToolArguments
{
    /// <summary>解析 HTTP 方法；不认识 ⇒ 拒绝。</summary>
    public static HttpMethodKind ParseMethod(string? raw)
    {
        var value = (raw ?? "GET").Trim().ToUpperInvariant();
        return value switch
        {
            "GET" => HttpMethodKind.Get,
            "HEAD" => HttpMethodKind.Head,
            "POST" => HttpMethodKind.Post,
            "PUT" => HttpMethodKind.Put,
            "PATCH" => HttpMethodKind.Patch,
            "DELETE" => HttpMethodKind.Delete,
            _ => throw new ToolUsageException(
                $"method 只支持 GET / HEAD / POST / PUT / PATCH / DELETE（收到 {value}）。"),
        };
    }

    /// <summary>枚举 → 线上方法名（请求用）。</summary>
    public static string MethodName(HttpMethodKind method) => method switch
    {
        HttpMethodKind.Get => "GET",
        HttpMethodKind.Head => "HEAD",
        HttpMethodKind.Post => "POST",
        HttpMethodKind.Put => "PUT",
        HttpMethodKind.Patch => "PATCH",
        HttpMethodKind.Delete => "DELETE",
        _ => throw new ToolUsageException("内部错误：未知 HTTP 方法。"),
    };

    /// <summary>解析返回形态。</summary>
    public static WebFetchFormat ParseFormat(string? raw)
    {
        var value = (raw ?? "text").Trim().ToLowerInvariant();
        return value switch
        {
            "text" => WebFetchFormat.Text,
            "html" => WebFetchFormat.Html,
            _ => throw new ToolUsageException("format 只支持 text / html。"),
        };
    }

    /// <summary>解析搜索时间范围（空 = 不限）。</summary>
    public static SearchRecency? ParseRecency(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value switch
        {
            "day" => SearchRecency.Day,
            "week" => SearchRecency.Week,
            "month" => SearchRecency.Month,
            "year" => SearchRecency.Year,
            _ => throw new ToolUsageException("recency 只支持 day / week / month / year。"),
        };
    }

    /// <summary>解析冲突策略。</summary>
    public static DownloadConflict ParseConflict(string? raw)
    {
        var value = (raw ?? "fail").Trim().ToLowerInvariant();
        return value switch
        {
            "fail" => DownloadConflict.Fail,
            "rename" => DownloadConflict.Rename,
            "overwrite" => DownloadConflict.Overwrite,
            _ => throw new ToolUsageException("conflict 只支持 fail / rename / overwrite。"),
        };
    }

    /// <summary>
    /// 解析请求头（一行一个 <c>Name: Value</c>；名字必须在白名单里）。
    /// <para>微格式只此一处；解析结果立刻变成 <see cref="RequestHeader"/> 记录，工具内部不再碰原文。</para>
    /// </summary>
    public static IReadOnlyList<RequestHeader> ParseHeaders(string? text, IReadOnlySet<string> allowed)
    {
        var result = new List<RequestHeader>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var index = line.IndexOf(':');
            if (index <= 0)
            {
                throw new ToolUsageException($"请求头行不是 \"Name: Value\" 形状：{line}");
            }

            var name = line[..index].Trim();
            if (!allowed.Contains(name))
            {
                throw new ToolUsageException(
                    $"请求头 \"{name}\" 不允许直接写（允许：{string.Join(", ", allowed)}；凭据请用 authRef）。");
            }

            result.Add(new RequestHeader(name, line[(index + 1)..].Trim()));
        }

        return result;
    }

    /// <summary>解析查询参数（一行一个 <c>name=value</c> 或 <c>name: value</c>）。</summary>
    public static IReadOnlyDictionary<string, string> ParseQuery(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index < 0)
            {
                index = line.IndexOf(':');
            }

            if (index <= 0)
            {
                result[line] = string.Empty;
                continue;
            }

            result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return result;
    }
}
