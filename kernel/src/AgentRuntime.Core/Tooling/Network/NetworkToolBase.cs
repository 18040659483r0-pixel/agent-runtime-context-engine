namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// **网络工具的模板基类**（模板方法）。
/// <para>
/// 派生类只做三件事：声明动作语义（<see cref="Operation"/>）、把参数变成 <see cref="NetworkRequest"/>、
/// 把 <see cref="NetworkResponse"/> 渲染成统一结果。**出网、校验、限额、凭据、审计全部在 <see cref="NetworkGateway"/> 里**。
/// </para>
/// <para>新增一个网络工具 = 新增一个类型；这里没有 <c>switch(工具名)</c>（铁则 8）。</para>
/// </summary>
public abstract class NetworkToolBase : ToolBase
{
    protected NetworkToolBase(NetworkGateway? gateway = null) =>
        Gateway = gateway ?? new NetworkGateway();

    /// <summary>唯一出网口（工具类里**不出现 HttpClient**）。</summary>
    protected NetworkGateway Gateway { get; }

    /// <summary>动作语义（读取 / 写远端 / 上传 / 表单）。</summary>
    public abstract NetworkOperation Operation { get; }

    /// <summary>默认对外动作（must-ask）；子类可按参数细分。</summary>
    public override ToolRisk Risk => ToolRisk.Outbound;

    /// <summary>按参数收紧本次上限（默认 = 网关出厂档）。</summary>
    protected virtual NetworkLimits LimitsFor(ToolArgs args) => Gateway.DefaultLimits;

    /// <summary>
    /// 构造请求（**只构造，不出网**；参数错误 ⇒ 抛 <see cref="ToolUsageException"/>）。
    /// <para>请求-响应形工具覆写它；形状不同的工具（如搜索后端适配）走默认实现会 fail-closed。</para>
    /// </summary>
    protected virtual NetworkRequest BuildRequest(ToolArgs args) =>
        throw new ToolUsageException($"工具 \"{Name}\" 没有请求-响应形状（内部错误）。");

    /// <summary>把统一响应渲染成工具结果（请求-响应形工具覆写它）。</summary>
    protected virtual ToolOutcome Interpret(NetworkResponse response, ToolArgs args) =>
        throw new ToolUsageException($"工具 \"{Name}\" 没有请求-响应形状（内部错误）。");

    /// <summary>
    /// 参数体检之后才执行；网络错误也走 <see cref="Interpret"/>（错误是结果）。
    /// <para>搜索类工具可覆写它（请求由可替换后端适配器发出，仍只经 <see cref="Gateway"/>）。</para>
    /// </summary>
    protected override async ValueTask<ToolOutcome> RunAsync(
        ToolContext context,
        ToolArgs args,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(args);
        var response = await Gateway
            .SendAsync(Operation, request, LimitsFor(args), cancellationToken)
            .ConfigureAwait(false);
        return Interpret(response, args);
    }

    /// <summary>统一的错误结果（不把异常抛给模型）。</summary>
    protected static ToolOutcome Failed(NetworkResponse response) =>
        ToolOutcome.Failure($"{response.Error ?? "网络请求失败。"}（耗时 {response.ElapsedMs} ms）");

    /// <summary>一行执行摘要（所有网络工具共用同一形状）。</summary>
    protected static string Trace(NetworkResponse response)
    {
        var cache = response.CacheHit ? " cache=hit" : string.Empty;
        var truncated = response.Truncated ? " truncated=true" : string.Empty;
        // 反爬页也写在 trace 里：模型一眼能看到「这一页不是正文」，不必自己猜。
        var bot = AntiBotGuard.Detect(response) is { } why ? $" antiBot={why}" : string.Empty;
        return $"status={response.Status} url={response.FinalUrl} bytes={response.Bytes} elapsed={response.ElapsedMs}ms{cache}{truncated}{bot}";
    }
}
