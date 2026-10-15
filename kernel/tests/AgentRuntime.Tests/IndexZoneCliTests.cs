using System.Text;
using AgentRuntime.Cli;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **文档索引区重建（B4 第一刀：Python → C#）** 的成对夹具。
/// <para>判据：① 有需重建时 <c>--check</c> 退 1、重建后退 0 且幂等；② **正文逐字节不变**；
/// ③ 缺 <c>memory/收尾水位.md</c> 时跳过不报错；④ <c>scripts/index-note.txt</c> 存在时逐字替换默认说明。</para>
/// <para>逐字节等价性另有真机证据：同输入下 C# 与 Python 产物 <c>cmp</c> 相同（BULLETIN 338,897 B / 水位 133,488 B）。</para>
/// </summary>
public sealed class IndexZoneCliTests
{
    private static string NewWs()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "idxz-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Directory.CreateDirectory(Path.Combine(root, "memory"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        return root;
    }

    private static string Body() =>
        "\n## 甲（2026-09-01）\n\n正文甲。\n\n## 乙（2026-09-02）\n\n正文乙。\n";

    [Fact]
    public void 重建_幂等_正文逐字节不变_缺水位文件跳过()
    {
        var root = NewWs();
        try
        {
            var bulletin = Path.Combine(root, "BULLETIN.md");
            var oldHead = "```\n## 🧭 索引（先读这里 · 正文在下方按需取）\n\n- 旧条目  ·  正文 L999\n\n---\n";
            File.WriteAllText(bulletin, oldHead + Body(), new UTF8Encoding(false));
            var bodyBefore = Body();

            // ① 需重建 ⇒ --check 退 1；缺水位文件 ⇒ 不算问题（跳过）。
            Assert.Equal(1, IndexZoneCli.Run(root, check: true));

            // ② 重建 ⇒ 退 0；索引区换成新锚点；正文一个字节没动。
            Assert.Equal(0, IndexZoneCli.Run(root, check: false));
            var after = File.ReadAllText(bulletin);
            Assert.EndsWith(bodyBefore, after, StringComparison.Ordinal);
            Assert.DoesNotContain("旧条目", after, StringComparison.Ordinal);
            // 行号锚点必须**真的**指向那一行（脚本二次校准的意义所在）：自校验，不用魔法数字。
            var anchorLine = Array.IndexOf(after.Split('\n'), "## 甲（2026-09-01）") + 1;
            Assert.True(anchorLine > 0, "正文标题必须在产物里");
            Assert.Contains($"正文 L{anchorLine}", after, StringComparison.Ordinal);

            // ③ 幂等：再跑一次无变化，文件字节不变。
            var bytes = File.ReadAllBytes(bulletin);
            Assert.Equal(0, IndexZoneCli.Run(root, check: false));
            Assert.Equal(bytes, File.ReadAllBytes(bulletin));
            Assert.Equal(0, IndexZoneCli.Run(root, check: true));           // 现在 --check 应为绿
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 说明行_优先用scripts下的备注文件()
    {
        var root = NewWs();
        try
        {
            File.WriteAllText(Path.Combine(root, "BULLETIN.md"),
                "## 🧭 索引（先读这里 · 正文在下方按需取）\n\n- x  ·  正文 L1\n\n---\n\n## 甲\n\n正文。\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "scripts", "index-note.txt"), "> 本文件不是 [OC] 那份。\n",
                new UTF8Encoding(false));

            Assert.Equal(0, IndexZoneCli.Run(root, check: false));
            var after = File.ReadAllText(Path.Combine(root, "BULLETIN.md"));
            Assert.Contains("> 本文件不是 [OC] 那份。", after, StringComparison.Ordinal);
            Assert.DoesNotContain("读法铁则", after, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
