using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace AgentRuntime.Core.Persistence;

/// <summary>
/// 内核**唯一**的序列化通道：0 依赖的确定性 XML（<see cref="DataContractSerializer"/>）。
/// <para>
/// 口径（主人 2026-09-29 定案）：core 内**不许出现 Python / JSON**，只许强类型编译型语言；
/// 序列化用**在箱内** <see cref="DataContractSerializer"/>，**0 NuGet 依赖**（内核硬约束）。
/// </para>
/// <para><b>三条硬纪律</b>：</para>
/// <list type="number">
/// <item><b>顺序显式</b>：落盘类型必须标 <c>[DataContract(Namespace = "")]</c>，且每个字段标
/// <c>[DataMember(Order = n)]</c> —— 字节可复现**不靠** CLR 元数据顺序。</item>
/// <item><b>字节纪律</b>：UTF-8 <b>无 BOM</b>、换行一律 <c>LF</c>、文件末尾一个 <c>LF</c>（与冻结层同源）。</item>
/// <item><b>只追加流 = XML 片段串联</b>：一条记录一个根元素，记录之间以 <c>LF</c> 隔开；
/// 读用片段模式 <see cref="XmlReader"/> 顺序取 —— <b>靠结构，不靠转义</b>，
/// 正文里的换行原样保留（这正是 JSON 要 <c>\n</c> 转义的病根）。</item>
/// </list>
/// <para>坏文件**不降级**：解析失败原样抛出（唯一真相源静默重置比报错贵得多，§十·23）。</para>
/// </summary>
public static class DataContractXml
{
    // 片段写：单行、无声明；正文里的换行不会被改写（结构承载，不转义）。
    private static readonly XmlWriterSettings FragmentWrite = new()
    {
        OmitXmlDeclaration = true,
        ConformanceLevel = ConformanceLevel.Fragment,
        Indent = false,
        NewLineChars = "\n",
        // 先让它写出来（否则 XmlWriter 当场抛），再由 Sanitize 把 XML 承载不了的字符换掉。
        CheckCharacters = false,
    };

    // 文档写：带声明、缩进可读；UTF-8 **无 BOM**（BOM 会改首字节 ⇒ 指纹漂）。
    private static readonly XmlWriterSettings DocumentWrite = new()
    {
        OmitXmlDeclaration = false,
        ConformanceLevel = ConformanceLevel.Document,
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        CheckCharacters = false,
    };

    private static readonly XmlReaderSettings FragmentRead = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true,
    };

    private static readonly XmlReaderSettings DocumentRead = new()
    {
        ConformanceLevel = ConformanceLevel.Document,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true,
    };

    /// <summary>
    /// XML 1.0 **承载不了**的字符（C0 控制符，除 TAB/LF/CR；以及孤立代理项）⇒ 一律换成 U+FFFD。
    /// <para>
    /// 这是**格式的硬边界**，不是转义问题：<c>XmlWriter</c> 遇到它们直接抛（真机数据里就有 <c>\u0001</c>）。
    /// 换掉会**丢那一个字符** ⇒ 调用方**必须响亮报数**（迁移器把它记进清单；运行时写在事件正文里）。
    /// </para>
    /// </summary>
    public static string Sanitize(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        // 按**码点**判：XML 1.0 的 Char 允许 #x10000–#x10FFFF ⇒ **合法代理对必须保留**，
        // 只换**孤立**代理项（实现与本文档口径此前不一致：按码元逐个判会把所有 emoji 折成 U+FFFD —— 静默丢字）。
        var needs = false;
        for (var i = 0; i < xml.Length; i++)
        {
            var c = xml[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < xml.Length && char.IsLowSurrogate(xml[i + 1])) { i++; continue; }
                needs = true;
                break;
            }

            if (char.IsLowSurrogate(c) || !IsXmlChar(c))
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return xml;
        }

        var sb = new StringBuilder(xml.Length);
        for (var i = 0; i < xml.Length; i++)
        {
            var c = xml[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < xml.Length && char.IsLowSurrogate(xml[i + 1]))
                {
                    sb.Append(c).Append(xml[i + 1]);
                    i++;
                    continue;
                }

                sb.Append('\uFFFD');
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                sb.Append('\uFFFD');
                continue;
            }

            sb.Append(IsXmlChar(c) ? c : '\uFFFD');
        }

        return sb.ToString();
    }

    /// <summary>XML 1.0 允许的字符（<c>Char</c> 产生式；**按码元**：代理对由调用方整对判）。</summary>
    private static bool IsXmlChar(char c) =>
        c == '\t' || c == '\n' || c == '\r'
        || (c >= 0x20 && c <= 0xD7FF)
        || (c >= 0xE000 && c <= 0xFFFD);

    /// <summary>按类型缓存的序列化器（构造昂贵；同类型复用）。</summary>

    private static class Cache<T>
    {
        public static readonly DataContractSerializer Instance = new(typeof(T));
    }

    private static DataContractSerializer For<T>() => Cache<T>.Instance;

    /// <summary>把一个对象写成**单行 XML 片段**（无声明；用于只追加流的每条记录）。</summary>
    public static string ToFragment<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, FragmentWrite))
        {
            For<T>().WriteObject(writer, value);
        }

        return Sanitize(sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// **行式片段**：同 <see cref="ToFragment{T}"/>，但把正文里的 LF/CR/TAB 转成字符引用
    /// （<c>&#10;</c>/<c>&#13;</c>/<c>&#9;</c>）⇒ **一份 = 一行**（「第 N 行 = 第 N 条」的寻址契约）。
    /// <para>
    /// [WB] 的事件流 / 派生层用 <see cref="ToFragment{T}"/>（结构承载、不转义，多行正文跨行）；
    /// **需要按行寻址的账本/记录**（benchmark <c>records</c>、[OC] skill-repo 派生层）用本方法。
    /// 读回一律 <see cref="ReadFragments{T}"/>/<see cref="FromFragment{T}"/> —— XML 语义等价，解析出的值相同。
    /// </para>
    /// </summary>
    public static string ToLineFragment<T>(T value) where T : class =>
        ToFragment(value)
            .Replace("\r", "&#13;", StringComparison.Ordinal)
            .Replace("\n", "&#10;", StringComparison.Ordinal)
            .Replace("\t", "&#9;", StringComparison.Ordinal);

    /// <summary>从单行 XML 片段还原对象。片段非法 ⇒ 抛 <see cref="InvalidDataException"/>。</summary>
    public static T FromFragment<T>(string xml) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var reader = XmlReader.Create(new StringReader(xml), FragmentRead);
        try
        {
            _ = reader.MoveToContent();
            return For<T>().ReadObject(reader) as T
                   ?? throw new InvalidDataException($"XML 片段解析为空或类型不符（{typeof(T).Name}）：{Head(xml)}");
        }
        catch (Exception ex) when (ex is XmlException or SerializationException)
        {
            throw new InvalidDataException($"XML 片段非法（{typeof(T).Name}）：{ex.Message}｜{Head(xml)}", ex);
        }
    }

    /// <summary>顺序读一个「只追加流」（XML 片段串联）。第一条坏片段 ⇒ 抛（调用方决定是否自愈归档）。</summary>
    public static IEnumerable<T> ReadFragments<T>(string path) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var reader = XmlReader.Create(path, FragmentRead);
        var serializer = For<T>();
        while (reader.MoveToContent() != XmlNodeType.None)
        {
            T? value;
            try
            {
                value = serializer.ReadObject(reader) as T;
            }
            catch (Exception ex) when (ex is XmlException or SerializationException)
            {
                throw new InvalidDataException($"事件流片段非法（{typeof(T).Name}）：{ex.Message}", ex);
            }

            yield return value
                         ?? throw new InvalidDataException($"事件流片段类型不符（{typeof(T).Name}）：{path}");
        }
    }

    /// <summary>顺序读一个「只追加流」的**文本形式**（与 <see cref="ReadFragments{T}"/> 同口径，直接从字符串读）。</summary>
    public static IEnumerable<T> ReadFragmentsFromText<T>(string xml) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var reader = XmlReader.Create(new StringReader(xml), FragmentRead);
        var serializer = For<T>();
        while (reader.MoveToContent() != XmlNodeType.None)
        {
            T? value;
            try
            {
                value = serializer.ReadObject(reader) as T;
            }
            catch (Exception ex) when (ex is XmlException or SerializationException)
            {
                throw new InvalidDataException($"XML 片段非法（{typeof(T).Name}）：{ex.Message}", ex);
            }

            yield return value
                         ?? throw new InvalidDataException($"XML 片段类型不符（{typeof(T).Name}）。");
        }
    }

    /// <summary>追加一个片段到流文件（不存在则创建；UTF-8 无 BOM，片段 + 一个 LF）。</summary>
    public static void AppendFragment<T>(string path, T value) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(full, ToFragment(value) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>写一个**文档**（带 XML 声明 + 缩进；覆盖写；UTF-8 无 BOM + 末尾 LF）。</summary>
    public static void WriteFile<T>(string path, T value) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, ToDocument(value), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 把一个对象写成**文档字符串**（带 XML 声明 + 缩进 + 末尾 LF）——
    /// 与 <see cref="WriteFile"/> 落盘的字节**逐字节相同**（迁移器要拿字串做双验，不能两处各拼一遍）。
    /// </summary>
    public static string ToDocument<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);

        // 走 **MemoryStream + UTF-8 无 BOM**：StringBuilder/StringWriter 会被 XmlWriter 当成 UTF-16
        // ⇒ 声明写成 encoding="utf-16" 而字节是 UTF-8（读回当场报「no Unicode byte order mark」）。
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, DocumentWrite))
        {
            For<T>().WriteObject(writer, value);
        }

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer.ToArray());
        return Sanitize(text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>
    /// **原子**写文档（临时文件 + 改名）：任何时刻磁盘上要么是旧的完整内容、要么是新的。
    /// <para>失败不留半成品；临时文件清不掉也不掩盖原始异常。</para>
    /// </summary>
    public static void WriteFileAtomic<T>(string path, T value) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{full}.tmp-{Guid.NewGuid():N}";
        try
        {
            WriteFile(temporary, value);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 清理失败不掩盖原始异常（残留临时文件比半截文件安全）。
                }
            }
        }
    }

    /// <summary>从**文档字符串**还原对象（与 <see cref="FromFragment"/> 的区别：带声明与缩进的整份文档）。</summary>
    public static T FromDocument<T>(string xml) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var reader = XmlReader.Create(new StringReader(xml), DocumentRead);
        try
        {
            _ = reader.MoveToContent();
            return For<T>().ReadObject(reader) as T
                   ?? throw new InvalidDataException($"XML 文档解析为空或类型不符（{typeof(T).Name}）：{Head(xml)}");
        }
        catch (Exception ex) when (ex is XmlException or SerializationException)
        {
            throw new InvalidDataException($"XML 文档非法（{typeof(T).Name}）：{ex.Message}", ex);
        }
    }

    /// <summary>读一个**文档**（不存在 ⇒ 抛 <see cref="FileNotFoundException"/>；坏 ⇒ 抛）。</summary>
    public static T ReadFile<T>(string path) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var reader = XmlReader.Create(path, DocumentRead);
        try
        {
            _ = reader.MoveToContent();
            return For<T>().ReadObject(reader) as T
                   ?? throw new InvalidDataException($"XML 文档解析为空或类型不符（{typeof(T).Name}）：{path}");
        }
        catch (Exception ex) when (ex is XmlException or SerializationException)
        {
            throw new InvalidDataException($"XML 文档非法（{typeof(T).Name}）：{ex.Message}", ex);
        }
    }

    private static string Head(string text) => text[..Math.Min(120, text.Length)];
}
