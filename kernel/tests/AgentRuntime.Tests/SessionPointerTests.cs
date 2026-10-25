using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **会话指针的「收尾完成度」**（2026-10-24 主人令）：只有 <c>/reset</c> 才关流；但 **reset 过而收尾没做成**
/// 的流，下次进入要**接回原流**把它做完，不许静默开新流把没结的活埋掉。
/// <para>兼容：旧指针文件里没有这个字段 ⇒ 反序列化默认 **true**（老行为：closed ⇒ 自动 start 新流）。</para>
/// </summary>
public sealed class SessionPointerTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "wb-pointer-" + Guid.NewGuid().ToString("N"), "session.xml");

    [Fact]
    public void 收尾未完成_落盘后读回仍是未完成_并说明会接回原流()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var store = new SessionPointerStore(path);
            store.Save(new SessionPointer("/tmp/stream.xml", Closed: true, DateTimeOffset.Now, 3, null, CloseoutComplete: false));

            var back = store.TryLoad();
            Assert.NotNull(back);
            Assert.True(back!.Closed);
            Assert.False(back.CloseoutComplete);
            Assert.Contains("收尾**未完成**", back.Describe(), StringComparison.Ordinal);
            Assert.Contains("接回原流", back.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void 旧指针文件没有这一项_默认视为收尾完成()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, """
                <?xml version="1.0" encoding="utf-8"?>
                <Session xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                  <StreamPath i:nil="true" />
                  <Closed>true</Closed>
                  <At xmlns:d2p1="http://schemas.datacontract.org/2004/07/System">
                    <d2p1:DateTime>2026-10-20T01:00:00Z</d2p1:DateTime>
                    <d2p1:OffsetMinutes>480</d2p1:OffsetMinutes>
                  </At>
                  <SessionNumber>1</SessionNumber>
                  <FromHandoverAt i:nil="true" />
                </Session>
                """);

            var back = new SessionPointerStore(path).TryLoad();
            Assert.NotNull(back);
            Assert.True(back!.CloseoutComplete is null or true);   // 旧文件没有这一项 ⇒ null ⇒ 视作完成
            Assert.Contains("自动 start", back.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
