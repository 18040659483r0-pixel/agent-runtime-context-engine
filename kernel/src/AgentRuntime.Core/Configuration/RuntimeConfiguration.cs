using System.Runtime.Serialization;
using System.Text;
using System.Xml.Serialization;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Core.Configuration;

/// <summary>
/// <c>config.xml</c> 的映射（V0：字段极少，够跑通即可；不做多 Provider 配置系统）。
/// <para>
/// 密钥不写进本文件：只写「去哪个环境变量 / 哪个本地文件取」，见 <see cref="ResolveApiKey"/>。
/// </para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class RuntimeConfiguration
{
    public const string DefaultFileName = "config.xml";

    /// <summary>Provider 协议标识；V0 只支持 "openai-compatible"。</summary>
    [DataMember]
    public string Provider { get; set; } = "openai-compatible";

    /// <summary>接口根地址，须含 API 前缀（如 https://api.deepinfra.com/v1/openai）；不要带 /chat/completions。</summary>
    [DataMember]
    public string BaseUrl { get; set; } = string.Empty;

    [DataMember]
    public string Model { get; set; } = string.Empty;

    /// <summary>读取密钥的环境变量名（默认 AGENTRUNTIME_API_KEY）。</summary>
    [DataMember]
    public string ApiKeyEnv { get; set; } = "AGENTRUNTIME_API_KEY";

    /// <summary>可选：从本地文件读密钥（首行；支持 ~ 展开）。环境变量优先。</summary>
    [DataMember]
    public string? ApiKeyFile { get; set; }

    [DataMember]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>**provider 连接阶段超时**（秒；默认 15）—— 连不上时快速失败，不等整轮总超时。</summary>
    [DataMember]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>采样温度；null = 不发送。</summary>
    [DataMember]
    public double? Temperature { get; set; }

    /// <summary>
    /// **推理档位**（OpenAI 兼容的 <c>reasoning_effort</c>：minimal/low/medium/high）。
    /// <para>缺省 / 空 ⇒ 不发该字段（端点默认档）。用于与 [OC] 侧 think 级别对齐（2026-10-11）。</para>
    /// </summary>
    [DataMember]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// 启用的模块名，顺序 = prompt 中的贡献顺序。**空数组 = 裸聊（等价 V0）**。
    /// 目前支持："system-rules"（顶端铁则/人格）、"session"（多轮会话）。
    /// </summary>
    [DataMember]
    public List<string> Modules { get; set; } = [];

    /// <summary>"system-rules" 模块注入的 system 文本；留空则该模块不注入。</summary>
    [DataMember]
    public string? SystemRules { get; set; }

    /// <summary>"session" 模块保留的最大轮数（0 = 不限）。</summary>
    [DataMember]
    public int SessionMaxTurns { get; set; }

    /// <summary>
    /// **模型上下文窗口**（token；0 = 未知）—— 「这条会话还装得下多少」的分母。
    /// <para>
    /// 它是**模型事实**（不是策略），所以放配置；而「达 20% 就提议收尾」是**内核策略常量**
    /// （<see cref="Protocol.ContextBudget.ProposeRatio"/>）—— 两者分开，避免「改配置 = 改协议」。
    /// </para>
    /// <para>窗口未知 ⇒ 宿主**不提议**收尾（不拍脑袋），其余行为一字不变。</para>
    /// </summary>
    [DataMember]
    public int ContextWindow { get; set; }

    /// <summary>上下文占窗口多少时提议收尾 + 重开（窗口未配 ⇒ 不提议）。</summary>
    public Protocol.BudgetStatus BudgetOf(int promptTokens, bool estimated = false) =>
        Protocol.BudgetStatus.Of(promptTokens, ContextWindow, estimated);

    /// <summary>
    /// 冻结区（V2）选择：**拉起前定**的专业领域与项目。
    /// <para>域选择会改变冻结前缀 ⇒ 缓存整体失效，所以只能在拉起前设置（将来 GUI 的定位）。</para>
    /// </summary>
    [DataMember]
    public FrozenConfiguration Frozen { get; set; } = new();

    /// <summary>
    /// 会话事件流（V3 Append Stream）：**只追加**的会话记录，可选落盘为 JSONL。
    /// </summary>
    [DataMember]
    public StreamConfiguration Stream { get; set; } = new();

    /// <summary>
    /// **会话生命周期（收尾三层）的工作区**：收尾件检查要扫的根（协议 v10 第 7 条）。
    /// <para>配了它 ⇒ <c>/closeout</c> 会扫 <c>memory/</c>（水位之后的日记）· <c>handoff/</c>（今天的条目）·
    /// <c>knowledge/</c>（版本 + 指纹事实）；留空 ⇒ **不校验**（明说，不假装查过）。</para>
    /// </summary>
    [DataMember]
    public LifecycleConfiguration Lifecycle { get; set; } = new();

    /// <summary>
    /// 运行时快照（V4 Runtime Snapshot）：**恢复点账本**（只记位置，不存正文）。
    /// <para>默认开：配了 <see cref="Stream"/> 路径就每轮成功后原子写一次 —— 意外中断没有预告，
    /// 手动写快照等于没有。</para>
    /// </summary>
    [DataMember]
    public SnapshotConfiguration Snapshot { get; set; } = new();

    /// <summary>
    /// 语义焦点（V4.1 Semantic Focus）：**导航层**（不删不改历史，只把注意力往关键事件上挪）。
    /// <para>默认值见规格书 §六；<c>policy=report</c> 时权重来自流里的模型自报。</para>
    /// </summary>
    [DataMember]
    public FocusConfiguration Focus { get; set; } = new();

    /// <summary>
    /// 当前尾部（V4.2 Current Tail · R4）：把「最活跃的工作状态」持久化在 Runtime 本地。
    /// <para>
    /// ⚠️ <b>配置里只有存储路径</b>：行数 / 字符上限与 <c>[TAIL]</c> 自报格式**由协议区声明**
    /// （代码常量，唯一声明处）—— 否则等于「用户有权改协议」。
    /// </para>
    /// </summary>
    [DataMember]
    public CurrentTailConfiguration CurrentTail { get; set; } = new();

    /// <summary>
    /// 动态草稿区（V4.3 Dynamic Draft · R5）：**唯一允许大删大改**的区域
    /// （讨论中的构想 / 未定稿需求 / 未验证假设）。
    /// <para>
    /// ⚠️ <b>配置里只有存储路径</b>：行数 / 字符上限与 <c>[DRAFT]</c> 自报格式**由协议区声明**
    /// （代码常量，唯一声明处）—— 口径与 <see cref="CurrentTail"/> 完全一致。
    /// </para>
    /// </summary>
    [DataMember]
    public DynamicDraftConfiguration DynamicDraft { get; set; } = new();

    /// <summary>
    /// 工具队列（2026-09-25）：模型一次回复里的调用超过每轮执行额度时，
    /// **额度内的照跑、余下按发出顺序排队**（可数、可复原，绝不静默丢）。
    /// <para>
    /// ⚠️ <b>配置里只有存储路径</b>：执行额度（<c>ProtocolText.ToolMaxCallsPerReply</c>）由协议区声明。
    /// 留空 = 只存内存（进程内可复原）；给了目录 = 跨进程可复原 + 坏文件报错。
    /// </para>
    /// </summary>
    [DataMember]
    public ToolQueueConfiguration ToolQueue { get; set; } = new();

    /// <summary>
    /// 知识树（v74「按 id 装载」的地址表来源）：<b>只含路径</b>，不含行数 / 上限之类的协议参数。
    /// <para>留空 = 不接「按 id 装载」（<c>[KN]</c> 自报照旧写在协议里，只是本会话不兑现）。</para>
    /// </summary>
    [DataMember]
    public KnowledgeConfiguration Knowledge { get; set; } = new();

    /// <summary>最小 Worker（便宜 Provider + 受限工具面 + 受限上下文）。见 <c>docs/DESIGN-MINIMAL-WORKER.md</c>。</summary>
    [DataMember]
    public WorkerConfiguration Worker { get; set; } = new();

    /// <summary>
    /// 网络能力（第一批 5 个网络工具）：搜索后端 / 超时 / 响应上限 / 下载根目录 / <c>authRef</c> 声明。
    /// <para>见 <c>docs/DESIGN-NETWORK-TOOLS.md</c> §二。凭据只写环境变量名，不写值。</para>
    /// </summary>
    [DataMember]
    public NetworkConfiguration Network { get; set; } = new();

    public static RuntimeConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到配置文件：{path}", path);
        }

        var xml = File.ReadAllText(path);
        RejectRemovedKeys(xml, path);

        RuntimeConfiguration? config;
        try
        {
            // **为什么是 XmlSerializer 而不是 DataContractSerializer**（对**手改的配置文件**）：
            // ① DataContract 反序列化**不跑构造函数** ⇒ 缺席元素处属性初始化器失效（null/0）；
            // ② DataContract **顺序敏感** ⇒ 元素一换序就**静默只读一部分**（静默丢设置）。
            // XmlSerializer：跑构造函数（默认值生效）+ 元素顺序无关；仍为强类型 XML、.NET 自带、零外部依赖。
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(RuntimeConfiguration));
            using var reader = new StreamReader(path, Encoding.UTF8);
            config = (RuntimeConfiguration?)serializer.Deserialize(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"配置文件不是合法 XML（{path}）：{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        if (config is null)
        {
            throw new InvalidDataException($"配置文件内容为空：{path}");
        }

        config.Validate();
        return config;
    }

    /// <summary>写回（A3 生成器用）：XmlSerializer 输出 —— 读回时**元素顺序无关**。</summary>
    public static void Save(RuntimeConfiguration config, string path)
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(RuntimeConfiguration));
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        serializer.Serialize(writer, config);
    }

    /// <summary>
    /// **已删除配置项的显式拒绝**（迁移闸门；A3 起配置走**强类型 XML**，本项按 XML 元素名扫）。
    /// <para>为什么不是「静默忽略」：留着这个键的用户会以为它还在起作用（实际上是设了白设）——与 PITFALLS #14 同一条纪律：删东西要让人知道。</para>
    /// </summary>
    private static void RejectRemovedKeys(string xml, string path)
    {
        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return; // 非法 XML 由后续反序列化给出更准确的话术。
        }

        var root = doc.Root;
        if (root is null)
        {
            return;
        }

        bool Has(string parent, string child) => root.Elements(parent)
            .SelectMany(e => e.Elements())
            .Any(e => string.Equals(e.Name.LocalName, child, StringComparison.OrdinalIgnoreCase));

        if (Has("Focus", "ReportHint"))
        {
            throw new InvalidDataException(
                $"配置项 focus.reportHint 已删除（{path}）：自报提示词改由**协议区**声明（代码常量，用户无权改）。" +
                "请从配置里移除该元素 —— 这里显式拒绝而不静默忽略。");
        }

        foreach (var key in RemovedCurrentTailKeys)
        {
            if (Has("CurrentTail", key))
            {
                throw new InvalidDataException(
                    $"配置项 currentTail.{key} 不允许存在（{path}）：R4 的行数/字符上限与 [TAIL] 自报格式" +
                    "由**协议区**声明（代码常量，用户无权改）；配置里只留 storePath。请从配置里移除该元素 —— 与 focus.reportHint 同口径，显式拒绝而不静默忽略。");
            }
        }

        foreach (var key in RemovedDynamicDraftKeys)
        {
            if (Has("DynamicDraft", key))
            {
                throw new InvalidDataException(
                    $"配置项 dynamicDraft.{key} 不允许存在（{path}）：R5 的行数/字符上限与 [DRAFT] 自报格式" +
                    "由**协议区**声明（代码常量，用户无权改）；配置里只留 storePath。请从配置里移除该元素 —— 与 currentTail / focus.reportHint 同口径，显式拒绝而不静默忽略。");
            }
        }
    }

    /// <summary>已被协议区接管、不得再出现在配置里的 R4 键（显式拒绝，见 <see cref="RejectRemovedKeys"/>）。</summary>
    private static readonly string[] RemovedCurrentTailKeys = ["maxLines", "maxChars", "reportHint"];

    /// <summary>已被协议区接管、不得再出现在配置里的 R5 键（同口径）。</summary>
    private static readonly string[] RemovedDynamicDraftKeys = ["maxLines", "maxChars", "reportHint"];

    public void Validate()
    {
        if (!string.Equals(Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"V0 只支持 provider=\"openai-compatible\"，当前为 \"{Provider}\"。");
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidDataException("config.xml 缺少 baseUrl。");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidDataException("config.xml 缺少 model。");
        }

        if (TimeoutSeconds <= 0)
        {
            throw new InvalidDataException("timeoutSeconds 必须为正整数。");
        }

        if (SessionMaxTurns < 0)
        {
            throw new InvalidDataException("sessionMaxTurns 不能为负数。");
        }

        if (ContextWindow < 0)
        {
            throw new InvalidDataException("contextWindow 不能为负数（0 = 未知 ⇒ 不提议收尾）。");
        }

        if (Lifecycle.Window is not ("closeout" or "always"))
        {
            throw new InvalidDataException($"lifecycle.window 只支持 \"closeout\" / \"always\"，当前为 \"{Lifecycle.Window}\"。");
        }

        var blankCommand = Lifecycle.Pipeline.FirstOrDefault(string.IsNullOrWhiteSpace);
        if (blankCommand is not null)
        {
            throw new InvalidDataException("lifecycle.pipeline 里有空命令：要么去掉，要么写清（空串会被当成「配过了」）。");
        }


        var unknown = Modules.FirstOrDefault(m => !KnownModules.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            throw new InvalidDataException($"未知模块：\"{unknown}\"（可用：{string.Join("、", KnownModules)}）。");
        }

        // 两个「会话历史的家」不得同时挂：同时开会把同一轮历史贡献两遍（且事件流与可变列表语义冲突）。
        var hasSession = Modules.Contains(SessionModuleName, StringComparer.OrdinalIgnoreCase);
        var hasStream = Modules.Contains(AppendStreamModuleName, StringComparer.OrdinalIgnoreCase);
        if (hasSession && hasStream)
        {
            throw new InvalidDataException(
                $"模块 \"{SessionModuleName}\" 与 \"{AppendStreamModuleName}\" 互斥：二者都是会话历史的家（前者可变内存列表，后者只追加事件流），同时挂会让历史重复。");
        }

        if (Stream.MaxEvents < 0)
        {
            throw new InvalidDataException("stream.maxEvents 不能为负数。");
        }

        try
        {
            Focus.ToOptions().Validate();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"focus 配置有误：{ex.Message}", ex);
        }
    }

    /// <summary>当前版本支持的模块名（新增模块时在这里登记）。
    /// <para>
    /// 注意：<c>protocol</c> 虽在此登记（写出它也合法），但它**不是可摘除的模块** —— 协议区由组合根强制装配，
    /// 列出与否结果完全一样（见 <c>ModuleRegistry</c> 的 P3 闸门）。
    /// </para>
    /// <para>
    /// <c>tool</c>（V7 工具面）在配置里的位置有讲究：它是**零注入**模块（不往 prompt 加字节），
    /// 因此按动态区 R2 类计次序 ⇒ 请把它写在 <c>append-stream</c> 之后、<c>current-tail</c> / <c>dynamic-draft</c> / <c>focus</c> 之前
    /// （写在后面会被确定性闸门当「动态区次序违规」报错 —— 那道闸门故意不纠正、只报错）。
    /// </para>
    /// </summary>
    public static readonly string[] KnownModules = ["protocol", "system-rules", "session", "append-stream", "tool", "worker-dispatch", "note", "rules", "knowledge", "decision", "memory-index", "focus", "current-tail", "dynamic-draft", "worker-knowledge", "decision-bypass", "task-ledger"];

    /// <summary>会话模块名（互斥校验用）。</summary>
    public const string SessionModuleName = "session";

    /// <summary>只追加事件流模块名（互斥校验用）。</summary>
    public const string AppendStreamModuleName = "append-stream";

    /// <summary>是否裸聊（一个模块都不挂）。</summary>
    public bool IsBare => Modules.Count == 0;

    /// <summary>
    /// 取密钥：环境变量（<see cref="ApiKeyEnv"/>）优先，其次 <see cref="ApiKeyFile"/>。
    /// 找不到返回 null，由调用方给出人类可读的提示（绝不在日志/异常里回显密钥本身）。
    /// </summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyFile))
        {
            var path = ExpandHome(ApiKeyFile);
            if (File.Exists(path))
            {
                var line = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>密钥缺失时给用户的指引（只说去哪取，不涉及密钥内容）。</summary>
    public string DescribeMissingApiKey() =>
        $"未找到 API Key：请设置环境变量 {ApiKeyEnv}，或在 config.xml 里指定 apiKeyFile 指向本地密钥文件" +
        "（该文件不要提交进 SVN）。";

    public static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}

/// <summary>
/// **会话生命周期（收尾三层）** 的配置：**只含路径**（协议文本与三层义务由协议区声明，配置无权改）。
/// <para>路径相对配置文件目录解析（与 <c>stream.path</c> / <c>frozen.root</c> 同规）；留空 = 不校验。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class LifecycleConfiguration
{
    /// <summary>WB 语料工作区根（<c>memory/</c> · <c>handoff/</c> · <c>knowledge/</c> 在它下面）；留空 = 不校验。</summary>
    [DataMember]
    public string? Workspace { get; set; }

    /// <summary>
    /// **收尾窗口的开放模式**：<c>closeout</c>（默认 = 只在 <c>/closeout</c> 那一刻开）｜<c>always</c>（整个会话常开）。
    /// <para>主人 2026-09-20 16:1x：「现在一次收尾要按十几次权限授权……我们需要的是一个更加宽泛的权限约束方式」——
    /// task 级的收尾件（handoff / 坑）发生在任务过程中，只在 <c>/closeout</c> 开窗 ⇒ 那些写还在窗外逐条问。
    /// <c>always</c> = 把「配置声明的那几类目标」在整个 tasklife 里静默放行（v13：分类仍算，但只留档、不拦截）。</para>
    /// </summary>
    [DataMember]
    public string Window { get; set; } = "closeout";

    /// <summary>踩坑集文件（如项目 <c>docs/PITFALLS.md</c>）；留空 = 不校验这一项。</summary>
    [DataMember]
    public string? Pitfalls { get; set; }

    /// <summary>
    /// **工具面拒绝的收件箱**（如 <c>docs/TOOLFACE-PITFALLS.md</c>）；留空 = 不写（不猜路径）。
    /// <para><b>主人 2026-10-15 令</b>：远端 AI 即使读了协议区仍会一上来先试 JSON、被拒；协议区讲了但遵从率低 ⇒
    /// 由**知识库系统兜底**：<b>任何工具面拒绝，除了记录，还要立刻落进踩坑集</b>，
    /// 下一个 session 再由**顶层知识库**限制一次它的行为。</para>
    /// <para>为什么要机械落盘而不是「提醒模型去记」：拒绝只进事件流就**等于没记**（同 worker 的教训：写进模型读不到的地方 = 只留给人看）。
    /// 一行一条、带时刻与拒因 ⇒ 收尾可逐条抽象进 L1/L2，也能被闸门数出来。</para>
    /// </summary>
    [DataMember]
    public string? PitfallsInbox { get; set; }

    /// <summary>
    /// **收尾流水线的命令**（收尾到期时由运行时**原样打印**，远端 AI 按序执行）：
    /// 如 <c>AgentRuntime.Cli --knowledge-repo … --closeout</c> → <c>--promote</c> → 派生 → 重建语料。
    /// <para>为什么不写死在协议/代码里：命令与路径是**这台机器的部署事实**（配置的活）；协议只说「按运行时给的命令做」。</para>
    /// <para>留空 = 不打印（收尾仍由人/AI 按文档手做）。</para>
    /// </summary>
    [DataMember]
    public List<string> Pipeline { get; set; } = [];

    /// <summary>
    /// **末态快照落点**（收尾时写、<c>start</c> 时读）：白板与草稿的最后状态。
    /// <para>留空 = 默认 <c>~/.agentruntime/handover.xml</c>（与水位线同目录）。</para>
    /// </summary>
    [DataMember]
    public string? Handover { get; set; }

    /// <summary>
    /// **配置里是否显式写了 <c>handover</c>**（生命周期集成是**选择加入**的）。
    /// <para>为什么要有这一位：会话指针住在末态快照旁边 ⇒ 没配 handover 时不读指针、不写指针 ——
    /// 否则**默认路径会落到真实 <c>~/.agentruntime</c>**，测试之间互相污染（实测：40 条测试被打红）。
    /// 显式配了才启用「跨进程续接」这一整套。</para>
    /// </summary>
    /// <summary>
    /// **内置收尾**（2026-10-11 主人定）：收尾**由运行时自己做** —— 收集未收敛内容 → **一次模型调用** →
    /// 运行时**自己覆盖那些 MD**（<c>CloseoutJob</c>）。**处理内容是文件 IO，不是起进程**（R2 只管起进程）。
    /// 默认 <c>false</c>：不显式打开时，收尾仍按旧口径（协议第 7/8 条，远端 AI 跑流水线）。
    /// </summary>
    [DataMember]
    public bool NativeCloseout { get; set; }

    [XmlIgnore]
    public bool HandoverExplicit { get; set; }
}

/// <summary>
/// 运行时快照（V4）的配置：**只含路径与开关，不含密钥**。
/// <para>路径相对 benchmark/CLI 配置文件目录解析（与 <c>frozen.root</c> / <c>stream.path</c> 同规）；
/// 留空 = 默认位置 <c>~/.agentruntime/snapshot.xml</c>（与水位线同目录）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class SnapshotConfiguration
{
    /// <summary>快照文件路径；留空 = <c>~/.agentruntime/snapshot.xml</c>。</summary>
    [DataMember]
    public string? Path { get; set; }

    /// <summary>是否每轮成功后自动写（默认 true）；关掉用 <c>--no-snapshot</c>。</summary>
    [DataMember]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 语义焦点（V4.1）的配置（**不含密钥**）。
/// <para>
/// <c>path</c> 是**加速缓存**（流才是真相源，两者不一致 ⇒ 以流为准并报告）；
/// 其余是选焦参数（默认值 = 规格书 §六）。
/// </para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class FocusConfiguration
{
    /// <summary>焦点缓存文件；留空 = 默认 <c>~/.agentruntime/focus.xml</c>。</summary>
    [DataMember]
    public string? Path { get; set; }

    /// <summary>策略：<c>report</c>（默认，模型自报权重）| <c>explicit</c>（只认人工设定）。</summary>
    [DataMember]
    public string Policy { get; set; } = "report";

    /// <summary>top-K（默认 16）。</summary>
    [DataMember]
    public int TopK { get; set; } = FocusOptions.DefaultTopK;

    /// <summary>权重下限（默认 1.0）。</summary>
    [DataMember]
    public double MinWeight { get; set; } = FocusOptions.DefaultMinWeight;

    /// <summary>半衰期（默认 40 turn）。</summary>
    [DataMember]
    public int HalfLifeTurns { get; set; } = FocusOptions.DefaultHalfLifeTurns;

    /// <summary><c>--focus E004,E007</c> 的显式覆盖（命令行，不入配置文件）。</summary>
    [XmlIgnore]
    public IReadOnlyList<string> Explicit { get; set; } = [];

    /// <summary>
    /// DataContract 反序列化**不跑构造函数**（对象由未初始化实例填充）⇒ 属性初始化器**不生效**。
    /// <para>本回调把「非配置字段」恢复成安全默认（否则 <c>Explicit</c> 会是 null ⇒ 下游 NRE）。</para>
    /// </summary>
    [OnDeserializing]
    private void OnDeserializing(StreamingContext _) => Explicit = [];

    /// <summary><c>--focus-clear</c>（命令行，不入配置文件）。</summary>
    [XmlIgnore]
    public bool Cleared { get; set; }

    /// <summary>算成纯数据的选焦参数（Core 侧只看 <see cref="FocusOptions"/>）。</summary>
    public FocusOptions ToOptions() => new()
    {
        TopK = TopK,
        MinWeight = MinWeight,
        HalfLifeTurns = HalfLifeTurns,
        Policy = ParsePolicy(Policy),
        Explicit = Explicit,
        Cleared = Cleared,
    };

    /// <summary>策略文本 → 枚举（非法即报错，不静默当默认）。</summary>
    public static FocusPolicy ParsePolicy(string? policy) => policy?.Trim().ToLowerInvariant() switch
    {
        null or "" or "report" => FocusPolicy.Report,
        "explicit" => FocusPolicy.Explicit,
        _ => throw new InvalidDataException($"focus.policy 只支持 \"report\" / \"explicit\"，当前为 \"{policy}\"。"),
    };
}

/// <summary>
/// 当前尾部（R4）的配置：**只含存储路径**（上限与协议文本由协议区声明，见 <c>ProtocolText</c>）。
/// <para>路径相对 CLI 配置文件目录解析（与 <c>stream.path</c> / <c>snapshot.path</c> 同规）；
/// 留空 = 默认位置 <c>~/.agentruntime/tail</c>（**目录**，每个 session 一个 <c>&lt;sessionId&gt;.xml</c>）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class CurrentTailConfiguration
{
    /// <summary>尾部存储目录；留空 = 默认 <c>~/.agentruntime/tail</c>。</summary>
    [DataMember]
    public string? StorePath { get; set; }

    /// <summary>
    /// <c>--tail-report on|off</c>：关掉模型自报（消融 / 纯人工模式的**运行开关**）。
    /// <para>关掉自报 ≠ 改协议（协议仍在协议区），所以它可以放配置 / 命令行；但默认值不给配置键，
    /// 只作命令行开关 —— 避免「配置项 = 用户改协议」的口径混乱。</para>
    /// </summary>
    [XmlIgnore]
    public bool ReportEnabled { get; set; } = true;
}

/// <summary>
/// 动态草稿区（R5）的配置：**只含存储路径**（上限与协议文本由协议区声明，见 <c>ProtocolText</c>）。
/// <para>路径相对 CLI 配置文件目录解析（与 <c>stream.path</c> / <c>currentTail.storePath</c> 同规）；
/// 留空 = 默认位置 <c>~/.agentruntime/draft</c>（**目录**，每个 session 一个 <c>&lt;sessionId&gt;.xml</c>）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class DynamicDraftConfiguration
{
    /// <summary>草稿存储目录；留空 = 默认 <c>~/.agentruntime/draft</c>。</summary>
    [DataMember]
    public string? StorePath { get; set; }

    /// <summary>
    /// <c>--draft-report on|off</c>：关掉模型自报（消融 / 纯人工模式的**运行开关**）。
    /// <para>关掉自报 ≠ 改协议（协议仍在协议区），所以它可以放命令行；但默认值不给配置键，
    /// 只作命令行开关 —— 与 <see cref="CurrentTailConfiguration.ReportEnabled"/> 完全同构。</para>
    /// </summary>
    [XmlIgnore]
    public bool ReportEnabled { get; set; } = true;
}

/// <summary>
/// 知识树（v74）的配置：**只含地址表路径**。
/// <para>路径相对 CLI/TUI 配置文件目录解析（与 <c>frozen.root</c> / <c>stream.path</c> 同规），
/// 可以是派生目录（<c>knowledge/.derived</c>）或 <c>nodes.xml</c> 文件。</para>
/// <para>留空 = 不接「按 id 装载」（无地址表 ⇒ <c>[KN]</c> 自报无处可查，不猜、不静默）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class KnowledgeConfiguration
{
    /// <summary>知识树派生目录或 <c>nodes.xml</c>；留空 = 未接。</summary>
    [DataMember]
    public string? Repo { get; set; }
}

/// <summary>
/// 会话事件流（V3）的配置。
/// <para>路径相对 benchmark/CLI 配置文件目录解析（与 frozen.root 同规）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class StreamConfiguration
{
    /// <summary>事件流文件（JSONL，只追加）。留空 = 只存内存（进程退出即丢）。</summary>
    [DataMember]
    public string? Path { get; set; }

    /// <summary>渲染窗口（0 = 全部渲染）；**不影响流本身**，只影响这次进 prompt 多少条。</summary>
    [DataMember]
    public int MaxEvents { get; set; }
}

/// <summary>
/// 冻结区（V2）的配置：**拉起前**选择项目层（2026-10-15：领域选择已退役）。
/// <para><see cref="Domains"/> 留空 = **全部加载**（默认；拿不准时的兜底）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class FrozenConfiguration
{
    /// <summary>
    /// 冻结语料根目录。相对路径由**调用方（Cli）按配置文件所在目录**解析成绝对路径。
    /// <para>留空 = 无磁盘语料（骨架 / 纯模块测试）。</para>
    /// </summary>
    [DataMember]
    public string? Root { get; set; }

    /// <summary>
    /// 收尾水位线文件路径（Runtime 层状态，**Session 之外**）。
    /// <para>相对路径同样由 Cli 按配置文件目录解析；留空 = 用默认位置。</para>
    /// </summary>
    [DataMember]
    public string? Watermark { get; set; }


    /// <summary>当前项目标识；null/空 = 不加载 Project 层。</summary>
    [DataMember]
    public string? Project { get; set; }
}
