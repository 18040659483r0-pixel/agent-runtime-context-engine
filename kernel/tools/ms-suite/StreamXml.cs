// 共享：读「行式 XML 片段流」（stream.xml / usage.xml 一族）。
// 口径与 python `xml.etree` 的 events() 一致：文本为空 ⇒ 空表；否则 `<root>` 包一层，取顶层子元素，
// 每个转成 {子元素名: 自身文本}（只取元素**自己的**文本 = `.text` 同义）。
using System.Text;
using System.Xml.Linq;

namespace MsSuite;

internal static class StreamXml
{
    public static List<Dictionary<string, string>> Events(string path)
    {
        var s = ReadRaw(path);
        if (string.IsNullOrWhiteSpace(s))
        {
            return [];
        }

        var root = XDocument.Parse("<root>" + s + "</root>").Root!;
        return root.Elements()
            .Select(e => e.Elements().ToDictionary(c => c.Name.LocalName, OwnText))
            .ToList();
    }

    private static string OwnText(XElement e) =>
        string.Concat(e.Nodes().TakeWhile(nd => nd is XText).Select(nd => nd.ToString()));

    /// <summary>读原文（不剥 BOM、非法字节按 U+FFFD 替换）—— 等同 python `open(encoding='utf-8', errors='replace')`。</summary>
    public static string ReadRaw(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
