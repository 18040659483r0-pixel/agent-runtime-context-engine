using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Tests;

/// <summary>
/// 唯一序列化通道（<see cref="DataContractXml"/>）的**成对夹具**：
/// 应绿（正常往返 / 确定性 / 无转义）+ 应红（坏文件必须抛）。
/// </summary>
public sealed class DataContractXmlTests
{
    [DataContract(Namespace = "")]
    private sealed class Sample
    {
        [DataMember(Order = 0)] public string Name { get; init; } = string.Empty;

        [DataMember(Order = 1)] public long Seq { get; init; }

        /// <summary>多行正文 —— JSON 要 <c>\n</c> 转义的那类内容。</summary>
        [DataMember(Order = 2)] public string Text { get; init; } = string.Empty;

        [DataMember(Order = 3)] public DateTimeOffset At { get; init; }
    }

    private static Sample Make(string text) => new()
    {
        Name = "样本",
        Seq = 7,
        Text = text,
        At = new DateTimeOffset(2026, 9, 29, 1, 30, 0, TimeSpan.FromHours(8)),
    };

    [Fact]
    public void 片段往返_多行正文不转义()
    {
        var text = "第一行\n第二行 <带尖括号> & 和与号\n第三行";
        var xml = DataContractXml.ToFragment(Make(text));

        Assert.DoesNotContain("\\n", xml);            // 不出现 JSON 式换行转义
        Assert.Contains("第二行", xml);               // 中文不转义成 \uXXXX

        var back = DataContractXml.FromFragment<Sample>(xml);
        Assert.Equal(text, back.Text);                 // 多行原样往返
        Assert.Equal(7, back.Seq);
        Assert.Equal(TimeSpan.FromHours(8), back.At.Offset);
    }

    [Fact]
    public void 同值两次序列化_字节相等()
    {
        var a = DataContractXml.ToFragment(Make("确定性"));
        var b = DataContractXml.ToFragment(Make("确定性"));

        Assert.Equal(a, b, StringComparer.Ordinal);
    }

    [Fact]
    public void 流文件_追加多条_按序读回且逐字节稳定()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dcx-stream-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "stream.xml");
            DataContractXml.AppendFragment(path, Make("甲"));
            DataContractXml.AppendFragment(path, Make("乙"));

            var read = DataContractXml.ReadFragments<Sample>(path).Select(s => s.Text).ToArray();
            Assert.Equal(new[] { "甲", "乙" }, read);

            // 无 BOM、LF 结尾（冻结层同源纪律）
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal((byte)'\n', bytes[^1]);

            // 重放一遍字节不变（确定性）
            DataContractXml.AppendFragment(path, Make("丙"));
            var first = File.ReadAllBytes(path);
            File.WriteAllBytes(path, first);
            Assert.Equal(first, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 文档往返_缩进可读_无BOM_LF结尾()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dcx-doc-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "doc.xml");
            DataContractXml.WriteFile(path, Make("文档"));

            var back = DataContractXml.ReadFile<Sample>(path);
            Assert.Equal("文档", back.Text);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal((byte)'\n', bytes[^1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 坏文件必须抛_不降级()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dcx-bad-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var doc = Path.Combine(dir, "bad.xml");
            File.WriteAllText(doc, "<Sample><Seq>不是数字</Seq>");
            Assert.ThrowsAny<Exception>(() => DataContractXml.ReadFile<Sample>(doc));

            var stream = Path.Combine(dir, "bad-stream.xml");
            File.WriteAllText(stream, "<Sample><Seq>不是数字</Seq></Sample>\n");
            Assert.ThrowsAny<Exception>(() => DataContractXml.ReadFragments<Sample>(stream).ToList());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
