using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Tooling.Network;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>网络策略负例（SSRF 的唯一判据；全部不出网）。</summary>
public sealed class NetworkPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192." + "168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void 私网与保留地址一律禁止(string address)
    {
        Assert.True(NetworkPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public void 公网地址放行(string address)
    {
        Assert.False(NetworkPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("ftp://example.com/a")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://example.com:8080/")]
    [InlineData("http://localhost/")]
    [InlineData("http://printer.local/")]
    public async Task 非法协议_凭据_端口_内网域名一律拒绝(string url)
    {
        var policy = new NetworkPolicy(new FixedResolver("93.184.216.34"));
        await Assert.ThrowsAsync<NetworkPolicyException>(async () => await policy.ValidateAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task DNS解析到私网也拒绝()
    {
        var policy = new NetworkPolicy(new FixedResolver("192." + "168.1.10"));
        await Assert.ThrowsAsync<NetworkPolicyException>(async () =>
            await policy.ValidateAsync("https://rebind.example/", CancellationToken.None));
    }

    [Fact]
    public async Task 公网地址通过()
    {
        var policy = new NetworkPolicy(new FixedResolver("93.184.216.34"));
        var uri = await policy.ValidateAsync("https://example.com/a?b=1", CancellationToken.None);
        Assert.Equal("example.com", uri.Host);
    }

}

/// <summary>网关：重定向逐跳复查、上限、缓存、凭据不回显。</summary>
public sealed class NetworkGatewayTests
{
    [Fact]
    public async Task 重定向到私网被拒绝()
    {
        using var gateway = new NetworkGateway(
            policy: new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: new StubHandler(request =>
            {
                if (request.RequestUri!.Host == "example.com")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("http://127.0.0.1/secret");
                    return redirect;
                }

                return TextResponse("should not get here");
            }));

        var response = await gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest { Method = "GET", Url = "https://example.com/start" },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Contains("禁止", response.Error);
    }

    [Fact]
    public async Task 响应体超过上限时截断并明说()
    {
        using var gateway = new NetworkGateway(
            new NetworkLimits { MaxResponseBytes = 10 },
            new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: new StubHandler(_ => TextResponse(new string('a', 100))));

        var response = await gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest { Method = "GET", Url = "https://example.com/" },
            cancellationToken: CancellationToken.None);

        Assert.True(response.Truncated);
        Assert.Equal(10, response.Bytes);
        Assert.Equal(10, response.Body.Length);
    }

    [Fact]
    public async Task GET缓存命中不再出网()
    {
        var calls = 0;
        using var gateway = new NetworkGateway(
            new NetworkLimits { CacheTtlSeconds = 60 },
            new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: new StubHandler(_ =>
            {
                calls++;
                return TextResponse("hello");
            }));

        var request = new NetworkRequest { Method = "GET", Url = "https://example.com/cache" };
        var first = await gateway.SendAsync(NetworkOperation.Read, request, cancellationToken: CancellationToken.None);
        var second = await gateway.SendAsync(NetworkOperation.Read, request, cancellationToken: CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal("hello", second.Body);
    }

    [Fact]
    public async Task authRef的值进请求头_但不出现在结果里()
    {
        string? seen = null;
        var credentials = new CredentialResolver(
        [
            new AgentRuntime.Core.Tooling.Network.NetworkCredential("svc", "Authorization", "Bearer", () => "top-secret-token"),
        ]);
        using var gateway = new NetworkGateway(
            new NetworkLimits { CacheTtlSeconds = 0 },
            new NetworkPolicy(new FixedResolver("93.184.216.34")),
            credentials,
            new StubHandler(request =>
            {
                seen = request.Headers.TryGetValues("Authorization", out var values)
                    ? string.Join(",", values)
                    : null;
                return TextResponse("ok");
            }));

        var response = await gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest
            {
                Method = "GET",
                Url = "https://example.com/",
                AuthRef = "svc",
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal("Bearer top-secret-token", seen);
        Assert.DoesNotContain("top-secret-token", response.Body);
        Assert.DoesNotContain("top-secret-token", string.Join("|", gateway.Audit.Select(a => a.ToString())));
    }

    [Fact]
    public async Task 未声明的authRef直接拒绝()
    {
        using var gateway = new NetworkGateway(
            policy: new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: new StubHandler(_ => TextResponse("ok")));

        var response = await gateway.SendAsync(
            NetworkOperation.Read,
            new NetworkRequest { Method = "GET", Url = "https://example.com/", AuthRef = "missing" },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Contains("authRef", response.Error);
    }

    internal static HttpResponseMessage TextResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        return response;
    }
}

/// <summary>5 个网络工具：参数 / 执行 / 结果 / 负例（测试不连真实网络）。</summary>
public sealed class NetworkToolTests
{
    private const string Page = """
        <html><head><title>Example Title</title>
        <meta name="description" content="A sample page"></head>
        <body><script>steal()</script><style>.x{}</style>
        <p>Hello <b>world</b>.</p>
        <a href="/one">one</a> <a href="/two">two</a>
        </body></html>
        """;

    [Fact]
    public async Task web_fetch提取标题正文与链接数()
    {
        using var gateway = PublicGateway(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page, Encoding.UTF8, "text/html"),
            };
            return response;
        });
        var tool = new WebFetchTool(gateway);
        var args = Args("<call><name>web_fetch</name><url>https://example.com/page</url></call>", "web_fetch");

        var outcome = await tool.ExecuteAsync(Context(), args);

        Assert.True(outcome.Ok);
        Assert.Contains("title=Example Title", outcome.Text);
        Assert.Contains("links=2", outcome.Text);
        Assert.Contains("Hello world", outcome.Text);
        Assert.DoesNotContain("steal()", outcome.Text);
    }

    [Fact]
    public async Task web_fetch对私网目标失败()
    {
        using var gateway = PublicGateway(_ => NetworkGatewayTests.TextResponse("ok"));
        var tool = new WebFetchTool(gateway);
        var outcome = await tool.ExecuteAsync(Context(), Args("<call><name>web_fetch</name><url>http://127.0.0.1/</url></call>", "web_fetch"));

        Assert.False(outcome.Ok);
        Assert.Contains("禁止", outcome.Text);
    }

    [Fact]
    public void http_request按方法定档且拒绝敏感头()
    {
        var tool = new HttpRequestTool(new NetworkGateway(new NetworkLimits(), new NetworkPolicy()));
        Assert.Equal(ToolRisk.Outbound, tool.RiskFor(Args("<call><name>http_request</name><method>GET</method><url>https://example.com</url></call>", "http_request")));
        Assert.Equal(ToolRisk.Critical, tool.RiskFor(Args("<call><name>http_request</name><method>DELETE</method><url>https://example.com</url></call>", "http_request")));

        var exception = Assert.Throws<ToolUsageException>(() =>
            tool.Validate(Args("<call><name>http_request</name><method>GET</method><url>https://example.com</url><headers>Authorization: Bearer x</headers></call>", "http_request")));
        Assert.Contains("authRef", exception.Message);
    }

    [Fact]
    public async Task http_request的authRef值不进结果()
    {
        string? seen = null;
        using var gateway = new NetworkGateway(
            new NetworkLimits { CacheTtlSeconds = 0 },
            new NetworkPolicy(new FixedResolver("93.184.216.34")),
            new CredentialResolver([new AgentRuntime.Core.Tooling.Network.NetworkCredential("svc", "Authorization", "Bearer", () => "secret-123")]),
            new StubHandler(request =>
            {
                seen = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
                return NetworkGatewayTests.TextResponse("done");
            }));

        var tool = new HttpRequestTool(gateway);
        var outcome = await tool.ExecuteAsync(
            Context(),
            Args("<call><name>http_request</name><method>POST</method><url>https://example.com/api</url><body>{\"a\":1}</body><authRef>svc</authRef></call>", "http_request"));

        Assert.Equal("Bearer secret-123", seen);
        Assert.DoesNotContain("secret-123", outcome.Text);
        Assert.Contains("status=200", outcome.Text);
    }

    [Fact]
    public async Task web_search返回结果_未配置时fail_closed()
    {
        var backend = new FakeBackend();
        using var gateway = PublicGateway(_ => NetworkGatewayTests.TextResponse("unused"));
        var tool = new WebSearchTool(gateway, backend);

        var outcome = await tool.ExecuteAsync(Context(), Args("<call><name>web_search</name><query>openclaw</query><count>2</count></call>", "web_search"));
        Assert.True(outcome.Ok);
        Assert.Contains("1. First", outcome.Text);
        Assert.Contains("2. Second", outcome.Text);

        var none = new WebSearchTool(gateway, new NoneSearchBackend());
        var failed = await none.ExecuteAsync(Context(), Args("<call><name>web_search</name><query>x</query></call>", "web_search"));
        Assert.False(failed.Ok);
        Assert.Contains("未配置搜索后端", failed.Text);
    }

    [Fact]
    public async Task web_download写盘_拒绝路径逃逸与类型不在白名单()
    {
        var root = Directory.CreateTempSubdirectory("wb-download-");
        try
        {
            using var gateway = new NetworkGateway(
                new NetworkLimits
                {
                    CacheTtlSeconds = 0,
                    DownloadRoots = [root.FullName],
                    AllowedDownloadTypes = ["application/pdf"],
                },
                new NetworkPolicy(new FixedResolver("93.184.216.34")),
                handler: new StubHandler(request =>
                {
                    var content = new ByteArrayContent([1, 2, 3, 4]);
                    content.Headers.ContentType = new MediaTypeHeaderValue(
                        request.RequestUri!.AbsolutePath.EndsWith(".pdf", StringComparison.Ordinal)
                            ? "application/pdf"
                            : "text/html");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }));

            var tool = new WebDownloadTool(gateway);
            var outcome = await tool.ExecuteAsync(
                Context(),
                Args("<call><name>web_download</name><url>https://example.com/a.pdf</url><path>a.pdf</path></call>", "web_download"));

            Assert.True(outcome.Ok, outcome.Text);
            Assert.True(File.Exists(Path.Combine(root.FullName, "a.pdf")));

            var escape = Assert.Throws<ToolUsageException>(() =>
                tool.Validate(Args("<call><name>web_download</name><url>https://example.com/a.pdf</url><path>../escape.pdf</path></call>", "web_download")));
            Assert.Contains("路径逃逸", escape.Message);

            var wrongType = await tool.ExecuteAsync(
                Context(),
                Args("<call><name>web_download</name><url>https://example.com/a.html</url><path>a.html</path></call>", "web_download"));
            Assert.False(wrongType.Ok);
            Assert.Contains("不在允许列表", wrongType.Text);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task web_search_extract逐条失败不整批失败()
    {
        using var gateway = PublicGateway(request =>
        {
            if (request.RequestUri!.Host == "bad.example")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page, Encoding.UTF8, "text/html"),
            };
        });
        var tool = new WebSearchExtractTool(gateway);
        var outcome = await tool.ExecuteAsync(
            Context(),
            Args("<call><name>web_search_extract</name><urls>https://good.example/\nhttps://bad.example/</urls></call>", "web_search_extract"));

        Assert.True(outcome.Ok);
        Assert.Contains("title=Example Title", outcome.Text);
        Assert.Contains("status=500", outcome.Text);
    }

    [Fact]
    public void 工具派生关系_5本机加5网络()
    {
        Assert.IsAssignableFrom<LocalToolBase>(new ReadTool());
        Assert.IsAssignableFrom<LocalToolBase>(new ListTool());
        Assert.IsAssignableFrom<LocalToolBase>(new WriteTool());
        Assert.IsAssignableFrom<LocalToolBase>(new EditTool());
        Assert.IsAssignableFrom<LocalToolBase>(new ExecTool());

        Assert.IsAssignableFrom<NetworkToolBase>(new WebFetchTool());
        Assert.IsAssignableFrom<NetworkToolBase>(new HttpRequestTool());
        Assert.IsAssignableFrom<NetworkToolBase>(new WebDownloadTool());
        Assert.IsAssignableFrom<NetworkToolBase>(new WebSearchExtractTool());
        Assert.IsAssignableFrom<NetworkToolBase>(new WebSearchTool(backend: new FakeBackend()));

        var names = ToolSet.Default().Select(t => t.Name).ToArray();
        Assert.Contains(ToolNames.WebFetch, names);
        Assert.Contains(ToolNames.WebSearch, names);
        Assert.Contains(ToolNames.HttpRequest, names);
        Assert.Contains(ToolNames.WebDownload, names);
        Assert.Contains(ToolNames.WebSearchExtract, names);
        Assert.DoesNotContain(ToolNames.WebFetch, ToolNames.WorkerFace);
    }

    [Fact]
    public void 配置里的Network段能装载并装配工具()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var config = RuntimeConfiguration.Load(Path.Combine(directory!.FullName, "config.xml"));
        Assert.Equal("none", config.Network.SearchBackend);

        var limits = config.Network.ToLimits();
        Assert.NotEmpty(limits.DownloadRoots);

        var credentials = CredentialResolver.FromConfiguration(config.Network);
        using var gateway = new NetworkGateway(limits, credentials: credentials);
        var search = HttpSearchBackend.Create(config.Network, gateway, credentials);
        Assert.False(search.IsConfigured);

        var tools = ToolSet.Subset(ToolNames.All, gateway, search);
        Assert.Equal(ToolNames.All.Length, tools.Count);
    }

    [Fact]
    public void XML成员是强类型_未声明成员一律拒绝()
    {
        var call = ToolArgs.Parse(
            "<call><name>web_fetch</name><url>https://example.com/</url><maxChars>100</maxChars><format>html</format></call>",
            "web_fetch");
        Assert.Equal("https://example.com/", call.RequireString("url"));
        Assert.Equal(100, call.OptionalInt("maxChars"));
        Assert.Equal("html", call.OptionalString("format"));

        // 静默丢键是最难查的坑 ⇒ 未声明成员必须当场拒，并列出该工具允许的名字。
        var exception = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(
            "<call><name>web_fetch</name><url>https://example.com/</url><bogus>1</bogus></call>",
            "web_fetch"));
        Assert.Contains("未声明", exception.Message);
        Assert.Contains("url", exception.Message);
    }

    private static ToolContext Context() => new(ToolLimits.Default, 1, "test");

    private static ToolArgs Args(string text, string tool) => ToolArgs.Parse(text, tool);

    private static NetworkGateway PublicGateway(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(
            new NetworkLimits { CacheTtlSeconds = 0 },
            new NetworkPolicy(new FixedResolver("93.184.216.34")),
            handler: new StubHandler(responder));

    private sealed class FakeBackend : IWebSearchBackend
    {
        public string Name => "fake";

        public bool IsConfigured => true;

        public ValueTask<IReadOnlyList<WebSearchResult>> SearchAsync(
            string query,
            int count,
            SearchRecency? recency,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<WebSearchResult>>(
            [
                new WebSearchResult(1, "First", "https://one.example/", "snippet one", "fake"),
                new WebSearchResult(2, "Second", "https://two.example/", "snippet two", "fake"),
            ]);
    }
}

internal sealed class FixedResolver(string address) : IHostResolver
{
    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        // 字面 IP 原样返回（与 SystemHostResolver 同口径）；其它主机名给固定公网地址。
        if (IPAddress.TryParse(host, out var literal))
        {
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>([literal]);
        }

        return ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse(address)]);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}
