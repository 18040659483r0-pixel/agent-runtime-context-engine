using System.Net;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tooling.Network;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **网络工具的三条护栏**（2026-10-27 真机教训）：
/// ① 失败要说清是「出口不通」还是「这条地址不可达」；
/// ② 反爬页不许冒充成功；
/// ③ 同一后端连续失败 ⇒ 暂停，不再在同一死路上打转。
/// <para>全部不出网：handler 与出口体检都是注入的。</para>
/// </summary>
public sealed class NetworkGuardTests
{
    private static NetworkGateway Gateway(HttpMessageHandler handler, EgressDiagnosis diagnosis) =>
        new(
            policy: new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: handler,
            egressProbe: _ => Task.FromResult(diagnosis));

    private static Task<NetworkResponse> GetAsync(NetworkGateway gateway) =>
        gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest { Method = "GET", Url = "https://example.com/page" },
            cancellationToken: CancellationToken.None).AsTask();

    // ---------------- ① 失败分类 ----------------

    [Fact]
    public async Task 代理不通_错误里点名代理而且带上内层原因()
    {
        var handler = new ThrowingHandler(new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        using var gateway = Gateway(handler, EgressDiagnosis.Proxy("代理 127.0.0.1:7897 未响应（出口不可达）"));

        var response = await GetAsync(gateway);

        Assert.NotNull(response.Error);
        Assert.Contains("unexpected EOF", response.Error, StringComparison.Ordinal);      // 内层原因，不只外层一句话
        Assert.Contains("127.0.0.1:7897", response.Error, StringComparison.Ordinal);      // 点名是哪个代理
        Assert.Contains("出口", response.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("这条地址本身不可达", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 出口正常_判定为地址不可达并叫人换来源()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Connection reset by peer"));
        using var gateway = Gateway(handler, EgressDiagnosis.Ok("出口正常（代理 127.0.0.1:7897）"));

        var response = await GetAsync(gateway);

        Assert.NotNull(response.Error);
        Assert.Contains("这条地址本身不可达", response.Error, StringComparison.Ordinal);
        Assert.Contains("换来源", response.Error, StringComparison.Ordinal);
    }

    // ---------------- ② 反爬页识别 ----------------

    [Fact]
    public void 反爬_最终地址跳到验证码页()
    {
        var response = new NetworkResponse
        {
            Status = 200,
            FinalUrl = "https://wappass.baidu.com/static/captcha/tuxing_v2.html?ak=x",
            Body = "<html>ok</html>",
        };

        Assert.NotNull(AntiBotGuard.Detect(response));
    }

    [Fact]
    public void 反爬_短页面含验证词也算()
    {
        var response = new NetworkResponse
        {
            Status = 200,
            FinalUrl = "https://example.com/s",
            Body = "请完成安全验证后继续访问",
        };

        Assert.NotNull(AntiBotGuard.Detect(response));
    }

    [Fact]
    public void 正常长页面_不误报()
    {
        var response = new NetworkResponse
        {
            Status = 200,
            FinalUrl = "https://example.com/article",
            Body = new string('文', 9000) + "文中提到验证码的实现方式",
        };

        Assert.Null(AntiBotGuard.Detect(response));
    }

    [Fact]
    public void 非2xx_不判反爬()
    {
        var response = new NetworkResponse { Status = 403, FinalUrl = "https://example.com/", Body = "安全验证" };

        Assert.Null(AntiBotGuard.Detect(response));
    }

    // ---------------- ④ 代理抖动：幂等请求重试一次 -------------

    [Fact]
    public async Task 传输层失败_幂等请求重试一次()
    {
        var handler = new FlakyHandler();
        using var gateway = Gateway(handler, EgressDiagnosis.Ok("出口正常（代理 127.0.0.1:7897）"));

        var response = await GetAsync(gateway);

        Assert.Null(response.Error);          // 第二次成功
        Assert.Equal(200, response.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task 非幂等请求_不重试()
    {
        var handler = new FlakyHandler();
        using var gateway = Gateway(handler, EgressDiagnosis.Ok("出口正常（代理 127.0.0.1:7897）"));

        var response = await gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest
            {
                Method = "POST",
                Url = "https://example.com/api",
                Body = "{}",
                ContentType = "application/json",
            },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(1, handler.Calls);      // POST 不替它重发
    }

    [Fact]
    public async Task 拿到4xx_不重试()
    {
        var handler = new StaticHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var gateway = Gateway(handler, EgressDiagnosis.Ok("出口正常"));

        var response = await GetAsync(gateway);

        Assert.Equal(404, response.Status);
        Assert.Equal(1, handler.Calls);      // 站点已给答案，重试无意义
    }

    // ---------------- ③ 同后端连败 ⇒ 暂停 ----------------

    [Fact]
    public async Task 搜索后端连败两次_第二次直接说暂停且不再发请求()
    {
        var handler = new ThrowingHandler(new HttpRequestException(
            "The SSL connection could not be established, see inner exception."));
        using var gateway = Gateway(handler, EgressDiagnosis.Proxy("代理 127.0.0.1:7897 未响应（出口不可达）"));
        var backend = new HttpSearchBackend(gateway, "wikipedia", "https://en.wikipedia.org/w/api.php");

        var first = await Assert.ThrowsAsync<WebSearchException>(
            () => backend.SearchAsync("q", 3, null, CancellationToken.None).AsTask());
        Assert.Contains("搜索请求失败", first.Message, StringComparison.Ordinal);

        var second = await Assert.ThrowsAsync<WebSearchException>(
            () => backend.SearchAsync("q", 3, null, CancellationToken.None).AsTask());
        Assert.Contains("搜索请求失败", second.Message, StringComparison.Ordinal);
        var callsAfterSecond = handler.Calls;   // 两次真发过

        // 第三次：已是「连续 2 次失败」⇒ 直接快失败，不再发请求。
        var third = await Assert.ThrowsAsync<WebSearchException>(
            () => backend.SearchAsync("q", 3, null, CancellationToken.None).AsTask());

        Assert.Contains("已暂停", third.Message, StringComparison.Ordinal);
        Assert.Contains("不要连续重复", third.Message, StringComparison.Ordinal);
        Assert.Equal(callsAfterSecond, handler.Calls);
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new HttpRequestException("The SSL connection could not be established, see inner exception.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            });
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw error;
        }
    }
}
