using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Models;

namespace AgentRuntime.Tests;

/// <summary>
/// **最小 Worker 的闸门**（设计稿 <c>docs/DESIGN-MINIMAL-WORKER.md</c> §四 的 W1~W5）。
/// <para>为什么值钱：Worker 的全部价值是「**省钱且不降质**」—— 而这两条都会被"静默降级"毁掉
/// （省没省看不出来、做没做成也看不出来）。所以这里钉的不是功能，是**边界**。</para>
/// <para>离线：不调模型、不连网（真机派发见 CLI 的 <c>--worker-run</c>）。</para>
/// </summary>
public sealed class MinimalWorkerTests
{
    private sealed class FakeClient : IModelClient
    {
        public string Name => "fake-worker";
        public string Reply { get; set; } = "[WORKER] conclusion: 干完了 | evidence: a.cs:1, b.cs:7 | changes: none | open: none\nkind: done";
        public bool Throw { get; set; }
        public List<ChatRequest> Seen { get; } = [];

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            if (Throw)
            {
                throw new ModelClientException("上游 500", 500, "{\"error\":\"boom\"}");
            }

            return Task.FromResult(new ChatResponse
            {
                Choices = [new ChatChoice { Message = ChatMessage.Assistant(Reply) }],
                Usage = new TokenUsage { PromptTokens = 120, CompletionTokens = 30 },
            });
        }
    }

    private static WorkerConfiguration Cfg(string home, bool enabled = true) => new()
    {
        Enabled = enabled,
        ProviderName = "fake",
        Provider = new WorkerProviderConfiguration { BaseUrl = "https://example.invalid/v1", Model = "cheap-1" },
        Home = home,
        MaxPromptTokens = 80000,
    };

    private static string NewHome() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wb-worker-" + Guid.NewGuid().ToString("N"))).FullName;

    private static WorkerRequest Req() => new() { Task = "把 X 改成 Y", Window = ["src/A.cs"], Tools = ["read", "edit"] };

    // ------------------------------------------------------------------ W1

    [Fact]
    public async Task W1_未启用时拒绝派发_调用方必须走原路径()
    {
        var home = NewHome();
        try
        {
            var worker = new MinimalWorker(new FakeClient(), Cfg(home, enabled: false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.DispatchAsync(Req()));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ W2

    [Fact]
    public async Task W2_用量与流都落在worker自己的目录_不混进Master()
    {
        var home = NewHome();
        try
        {
            var client = new FakeClient();
            var worker = new MinimalWorker(client, Cfg(home));
            var result = await worker.DispatchAsync(Req());

            Assert.True(result.Ok);
            Assert.NotNull(result.StreamPath);
            // 落点分两处（2026-09-29 持续流改版后）：**流**在 <home>/<provider>/stream.xml（按 provider 分家、跨派发持续），
            // **本次派发的账**在 <home>/<provider>/<workerId>/（回执自带 LogPath ⇒ 不由调用方反推）。
            Assert.StartsWith(Path.Combine(home, "fake"), result.StreamPath!, StringComparison.Ordinal);
            Assert.Equal(
                Path.Combine(home, "fake", WorkerWatermark.StreamFileName),
                result.StreamPath!,
                StringComparer.Ordinal);
            Assert.NotNull(result.LogPath);
            var dir = result.LogPath!;
            Assert.True(File.Exists(Path.Combine(dir, "usage.xml")), "用量必须单独记账（W2）");
            Assert.True(File.Exists(result.StreamPath!));

            // 「谁花的钱」可查：用量文件里有 provider/model/用量
            var usage = File.ReadAllText(Path.Combine(dir, "usage.xml"));
            Assert.Contains("cheap-1", usage, StringComparison.Ordinal);
            Assert.Contains("PromptTokens", usage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ F4（面已迁到 Worker · 主人 2026-09-29 20:0x）

    [Fact]
    public async Task Worker面_需批动作_留档后照跑_且真的写了()
    {
        // 为什么在这边：master 的工具面已剥到只剩只读（index/scope）⇒ v13 的「**留档 + 照跑**」
        // 在 master 侧已无对象可测；这条不变量改在 **Worker 面**继续看着 —— 那里才是真正动手的那一半。
        var home = NewHome();
        var work = NewHome();
        try
        {
            var target = Path.Combine(work, "new.txt");
            var client = new FakeClient
            {
                Reply = $"[TOOL] write\npath: {target}\ncontent: ok\n"
                    + "[WORKER]\nconclusion: done\nanswer: 写了一个文件\nchanges: new.txt\nkind: done\n"
                    + $"evidence: {target}:1\ncovered: 1/1\nopen: none",
            };
            var worker = new MinimalWorker(client, Cfg(home));

            var result = await worker.DispatchAsync(new WorkerRequest { Task = "写一个文件", Window = [work], Tools = ["write"] });

            Assert.True(result.Ok);
            Assert.True(File.Exists(target), "Worker 面的需批动作必须真的执行（留档 ≠ 拦）");
            Assert.Equal("ok", File.ReadAllText(target));

            var stream = File.ReadAllText(result.StreamPath!);
            Assert.Contains("PermissionFiled", stream, StringComparison.Ordinal);   // 留档在它自己的流里
            Assert.Contains("ToolResult", stream, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(work, recursive: true);
        }
    }

    // ------------------------------------------------------------------ W3

    [Fact]
    public async Task W3_派发时只装worker协议区与任务_不含任何常驻知识()
    {
        var home = NewHome();
        try
        {
            var client = new FakeClient();
            var worker = new MinimalWorker(client, Cfg(home));
            await worker.DispatchAsync(Req());

            var sent = client.Seen.Single();
            // 两条：worker 协议区（system）+ 任务（user）。**没有第三条** ⇒ 没有知识/铁则/记忆索引。
            Assert.Equal(2, sent.Messages.Count);
            Assert.Equal("system", sent.Messages[0].Role);
            Assert.Contains("[WORKER]", sent.Messages[0].Content, StringComparison.Ordinal);
            Assert.Equal("user", sent.Messages[1].Role);
            // 关键反证：**主协议区的正文一个字都不许出现**（Worker 有它自己的worker 协议区）
            Assert.DoesNotContain("agent-runtime contract (kernel-owned", sent.Messages[0].Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ W4

    [Fact]
    public async Task W4_调用失败必须响亮_不静默降级成Master自己干()
    {
        var home = NewHome();
        try
        {
            var worker = new MinimalWorker(new FakeClient { Throw = true }, Cfg(home));
            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
            Assert.Contains("500", result.Failure!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task W4b_没交回WORKER块也算失败_但定责说清是契约不是任务()
    {
        var home = NewHome();
        try
        {
            var client = new FakeClient { Reply = "我改好了，就这样。" };
            var worker = new MinimalWorker(client, Cfg(home));
            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);
            Assert.Contains("[WORKER]", result.Failure!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ 空回复

    [Fact]
    public async Task 空回复不进流_也不炸整轮()
    {
        // 拔牙自证：修之前引擎会抛 ArgumentException（"parameter 'text'"）——
        // 实测真机首跑即撞（上游偶发空回复 ⇒ 整轮炸掉）。空内容是**模型行为**，不是数据错误。
        var home = NewHome();
        try
        {
            var worker = new MinimalWorker(new FakeClient { Reply = "" }, Cfg(home));
            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);                                  // 没结算 ⇒ 失败
            Assert.Contains("[WORKER]", result.Failure!, StringComparison.Ordinal);
            Assert.True(File.Exists(result.StreamPath!));              // 流文件在（但**没有**空正文事件）
            var stream = File.ReadAllText(result.StreamPath!);
            Assert.DoesNotContain("AgentOutput", stream, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ 预算闸门

    [Fact]
    public async Task 预算超限_拒绝派生_不静默截断()
    {
        var home = NewHome();
        try
        {
            var cfg = Cfg(home);
            cfg.MaxPromptTokens = 1;                       // 荒谬的小 ⇒ 必然超
            var client = new FakeClient();
            var worker = new MinimalWorker(client, cfg);

            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);
            Assert.Contains("maxPromptTokens", result.Failure!, StringComparison.Ordinal);
            Assert.Empty(client.Seen);                     // **没发出去** —— 拒绝在派发之前
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ 契约与配置

    /// <summary>
    /// **口径对齐闸门**（主人 2026-09-29 令：worker 就照 master 那边怎么教的教）。
    /// <para>钉两件事：① 工具面形状与 master 侧拒绝话术**逐字同一份**（`ToolFaceClause.Shape`，唯一来源）；
    /// ② 每把工具的键枚举与**主协议第 5 条同一批字面**（两处枚举一旦分叉，这里当场红）。</para>
    /// </summary>
    [Fact]
    public void 口径对齐_worker协议区与master工具面同源()
    {
        var worker = WorkerProtocolText.Text;

        // ① 形状：同一份（不是「抄得像」—— 就是它）
        Assert.Contains(AgentRuntime.Core.Tooling.ToolFaceClause.Shape, worker, StringComparison.Ordinal);

        // ② 键枚举：**各按各面**、同一份来源（v31 起 master 正文只列 index/scope，
        //    因为它的真实面就只剩这两把 —— 旧写法「两边都含 read{path」正是「口径分裂」的标本）
        Assert.Contains(AgentRuntime.Core.Tooling.ToolFaceClause.Keys, worker, StringComparison.Ordinal);
        Assert.Contains(AgentRuntime.Core.Tooling.ToolFaceClause.MasterKeys,
                        AgentRuntime.Core.Protocol.ProtocolText.Text, StringComparison.Ordinal);
        // 2026-10-10（主人令「把工具交回 master」）：master 面 = **全量** ⇒ 正文第 5 条列出全部 7 把。
        Assert.Contains("read{path", AgentRuntime.Core.Protocol.ProtocolText.Text, StringComparison.Ordinal);

        // ③ 节奏与省字（主协议第 2/11 条的口径）也在卡里：结果带指针 / 先定位再读窗口 / risk 声明
        Assert.Contains("offset", worker, StringComparison.Ordinal);
        Assert.Contains("<risk>", worker, StringComparison.Ordinal);
        Assert.Contains("绝不整份倒", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void worker协议区_在预算内_且只说怎么回话()
    {
        Assert.True(WorkerProtocolText.LineCount <= WorkerProtocolText.DeclaredMaxLines, $"worker 协议区 {WorkerProtocolText.LineCount} 行，超了就是在塞私货（v22：补 worker 侧五条 + 共用段 [SPLIT]；v39：[PACE]）");
        // v13（2026-09-29 · 主人 19:59 令）：`answer:` 语义按工具分级（write/edit ⇒ 改/写了什么的概括）⇒ 实测后预算 2600 → 2800。
        // v14（2026-09-30 01:3x · 主人令「worker 的协议区也要改，让 worker 也理解 master 是怎么使用它的」）：
        //      补第 9 条（回执是 master 的唯一视图：它会复算你的 evidence、按 MUST 核 covered、读 open；
        //      回执薄 ⇒ 下次派发更具体；强 ⇒ 按意图派）⇒ 实测 3,167 字符 ⇒ 预算 2800 → 3200（先量后定）。
        // v15（2026-09-30 · 主人令「改协议区让 worker 不再犯病」）：① 锚只能引**本轮**工具回执（未实测 ⇒ unknown / open:）；
        //      ② 读数用**键**给（bytes=/lines=/sha256=/at=）且以运行时 `measured:` 行为准；③ 带副作用的步骤**先查后做**。
        //      实测 4,007 字符 ⇒ 预算 3200 → 4400（先量后定；留 ~10% 余量）。
        // v16（2026-09-30）：新增第 11 条「工具被拒不是 done」（配套运行时：ToolDenied + open 为空 ⇒ Ok=false）
        //       ⇒ 实测 4,326 字符 ⇒ 预算仍为 4,400（在预算内，不动）。
        Assert.True(WorkerProtocolText.CharCount <= WorkerProtocolText.DeclaredMaxChars, $"worker 协议区 {WorkerProtocolText.CharCount} 字符（v39 预算 {WorkerProtocolText.DeclaredMaxChars} —— 实测见上：交付键成员化 + 示范改 XML + [PACE]，先量后定）");
        Assert.Contains("[WORKER]", WorkerProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("[TOOL]", WorkerProtocolText.Text, StringComparison.Ordinal);

        // v4 四字面（09 §四「应红」判据）：少了任一项 ⇒ 红。
        // 判据按**字面**量（契约是给模型读的英文文本，形状就是判据），不看措辞好不好。
        Assert.Contains("lifecycle", WorkerProtocolText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whiteboard", WorkerProtocolText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pitfalls", WorkerProtocolText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("handoff", WorkerProtocolText.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **契约正文指纹 × 版本号**（B7，2026-09-27）。
    /// <para>来历与主协议 <c>Gate_Protocol_VersionTracksTextBytes</c> 同：账本字段（版本）必须与它描述的事实（正文字节）对得上；
    /// 改正文不升版本 ⇒ 屏上版本、文档、代码三处互斥且**没有任何闸门会喊**。</para>
    /// <para>判据：正文 sha256 必须与**它自己声明的版本号**对得上；改正文不登记 ⇒ 红；只升版本不登记 ⇒ 红。</para>
    /// </summary>
    [Fact]
    public void Gate_ProtocolZone_VersionTracksTextBytes()
    {
        var actual = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(WorkerProtocolText.Text))).ToLowerInvariant();

        Assert.True(
            KnownProtocolZoneFingerprints.TryGetValue(WorkerProtocolText.Version, out var expected),
            $"契约正文声明的版本 \"{WorkerProtocolText.Version}\" 不在指纹表里 —— 改了正文就必须：" +
            "① 升 WorkerProtocolText.Version；② 把新指纹登记进 KnownProtocolZoneFingerprints；实测 = " + actual);

        // 用 Assert.True + 自写消息：Assert.Equal 会把长串截成 "···"，改正文的人拿不到完整指纹（当场踩过）。
        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"契约正文指纹不符：登记 v{WorkerProtocolText.Version} = {expected}；实测 = {actual}");
    }

    [Fact]
    public void 任务收口_失败落弃单并清区状态_成功只清_且幂等()
    {
        var root = Directory.CreateTempSubdirectory("wb-close-").FullName;
        var id = "20261009-130000-abcdef";
        foreach (var sub in new[] { "current-tail.xml", "draft.xml" })
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
            File.WriteAllText(Path.Combine(root, sub, id + ".xml"), "<x/>");
        }

        // 失败 ⇒ 清区状态 + 落一条弃单（调两次也只留一行）。
        MinimalWorker.CloseTaskRegion(root, id, ok: false, "ProviderTimeout", "上一件没做完的活", DateTimeOffset.Now);
        MinimalWorker.CloseTaskRegion(root, id, ok: false, "ProviderTimeout", "上一件没做完的活", DateTimeOffset.Now);

        Assert.False(File.Exists(Path.Combine(root, "current-tail.xml", id + ".xml")));
        Assert.False(File.Exists(Path.Combine(root, "draft.xml", id + ".xml")));
        var log = File.ReadAllLines(Path.Combine(root, "abandoned.log"));
        Assert.Single(log);
        Assert.Contains(id, log[0], StringComparison.Ordinal);
        Assert.Contains("abandoned=yes", log[0], StringComparison.Ordinal);
        Assert.Contains("ProviderTimeout", log[0], StringComparison.Ordinal);

        // 成功 ⇒ 只清、不落弃单。
        var id2 = "20261009-130001-fedcba";
        File.WriteAllText(Path.Combine(root, "draft.xml", id2 + ".xml"), "<x/>");
        MinimalWorker.CloseTaskRegion(root, id2, ok: true, null, "干完的活", DateTimeOffset.Now);
        Assert.False(File.Exists(Path.Combine(root, "draft.xml", id2 + ".xml")));
        Assert.Single(File.ReadAllLines(Path.Combine(root, "abandoned.log")));
    }

    /// <summary>版本 → 正文 sha256（**改正文必须登记**；见 <see cref="Gate_ProtocolZone_VersionTracksTextBytes"/>）。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> KnownProtocolZoneFingerprints =
        new(System.StringComparer.Ordinal)
        {
            // 注意：指纹量的是 **WorkerProtocolText.Text 的运行值**（C# 原始字符串会剥掉闭合定界符那层缩进），
            // 不是源码窗口里带缩进的原文 —— 2026-09-27 我用源码窗口量，登记错了指纹、当场红。
            ["2"] = "e7444899b1602414fda65ccb1eb6075103b68bed7cb7643cf6eba5d4d18ea320",
            ["3"] = "52da72c32b80cdee1529f0ae53835baae869df3877101de2da12d71424b4e9ea",
            // v4（2026-09-28）：Worker 是**持续状态的 engine** —— 跨轮一个 lifecycle / whiteboard / pitfalls: / handoff:。
            ["4"] = "0981e5dbcaa17ebc46b14855be10f52b189a55162f43a1c903b055ccb20d14b4",
            ["6"] = "4f4708b60fe51662caa89d3797990bb120b8892deb366b4f883e208f88394f3e",
            // v7（2026-09-29）：工具块改成**行式键值**（与 B1 同规，去 JSON）+ open: 未清会被退回。
            ["7"] = "4ba205976d108413b442745529795c9dcba0caab9ca9d4d9de792654d74de1cf",
            // v8（2026-09-29）：真机根因 —— ① 取事实的路（单条结果 ~2000 字符上限，别整份倒；用 grep/sed 取需要的行）；
            // ② 回执改**逐行 key: value**（值 = 该行其余部分 ⇒ 值里的 `|` 不再歧义）+ 必填 `covered:`。
            ["8"] = "dee0c253b7d673a4510c878501323c898448135344e4d3a4db0cdd0ab9cbeaf8",
            // v9（2026-09-29）：工具面**照 master 口径全教**（形状/键枚举/节奏都取自唯一来源 `ToolFaceClause`）。
            ["9"] = "5d16949e398a6c9f01860b64b2df6d5cdd9ed93f0303f01269d8f537c54b7408",
            // v10（2026-09-29 v29）：派发物新增 `MUST:` 段（应有集合）⇒ 段名清单改了。
            ["10"] = "063fe814a955a80e54d2894bcdd50defff9ddcf1adf480e4352f90e3954be314",
            // v11（2026-09-29）：回执加 `answer:`（结论正文）+ `evidence` 明确为锚 + ASK 段。
            ["11"] = "fd63eaaad4781612c795fa7beff6ae8333f42f7a784ccc1d28f105eed8401d77",
            // v13（2026-09-29 · 主人 19:59）：`answer:` 语义**按工具分级** —— write/edit ⇒ 改/写了什么的概括（不写 diff）；
            // exec ⇒ 执行了什么/结果；read/list ⇒ 发现/结论。
            ["14"] = "d39a9c084609dbe7cf4a6d730be1e2fc1d7ddb74e7c3f242bdbff329696ab501",
            // v15（2026-09-30）：把「重放 + 读数时点差」写进契约 —— 锚只能引本轮工具回执（未实测 ⇒ unknown / open:）、
            // 读数用键（bytes=/lines=/sha256=/at=）且以运行时 measured: 行为准、带副作用步骤先查后做。
            ["16"] = "9fc68377b908440412ff21eef50ff659b283dc616db30f40a4dafa7c1b54288b",
            // v17（2026-09-30）：回执改 [RESULT] 句 + kind= + 按类取字段（解「失败回执活不过终局」）；预算 4400 → 5400。
            ["17"] = "5520473d9c33566ba5c8e9bc25d51d0f08e65e42459544ce156234b57013550a",
            // v18（2026-09-30 16:47 主人口径）：`done` 必须给出**该 ASK 种类的交付键** + `basis=`；缺 ⇒ 不算 done、退回；
            // 没有"不需要"这一档（键不适用 = 选错了类或 criteria）；形状：一行一键，键与分隔符之间可有空白。
            ["18"] = "573892f0bc38f20e6ed07e90e1a10b68f22f25ab5b9579b9139b5b361b6421af",
            // v19（2026-09-30 16:58 主人令）：master-worker 模式协议**两边同一份**（MasterWorkerProtocol 渲染）。
            ["20"] = "418c057faa3c14aeb4bfb1d90308da2a562c48d0c99af10905f3f0658ea312a2",
            // v21（2026-10-06）：共用段新增 `[DUTY]` 节 —— 形状 vs 实质的分工（Worker 自证 / master 最小代价复算 /
            // 运行时只管形状层，**不得 infer/兜底实质**）。worker 协议区与 master 协议两侧逐字同一份。
            ["27"] = "dfbc4bdc30bd49cbce48b2495fbb8622beea764ba57d819a067b07fc2fb8bf51",
            // v28（2026-10-07 15:15）：共用段新增**回执组合的轴**（与派发同形）。
            ["28"] = "19a1ad20f6124c39453ff185c207f965b4b194215a38369fa16e80698979ba89",
            // v29（2026-10-07 15:30 审计）：[CHAIN] 补 worker 三种停 + [SPLIT] 点明机器决策报告。
            ["29"] = "0722952d462c3ce67dc8eefbdae7934f65f58a9d1dbd5c1e36b75367407d5f5b",
            // v30（2026-10-07 15:40 审计）：§7 写明**终局三态**（impossible 是终局、不与非终局并列）；§12 partial 非终局。
            ["30"] = "986394cbb5f1d37910decdca1022a7b3e60a891257f1ea05904ad2baa916ab30",
            // v31（2026-10-07 15:50）：共用段新增 [RULING] 临时回执 + §7 补「need_human 必附 [RULING]」。
            ["31"] = "a082b115f8738b5423f3c4eb5b1d8b68fd3e488c86f370421fef174aa12f829d",
            // v32（2026-10-07 16:05）：共用段新增 信封 / 裁定回信 / 作废。
            ["32"] = "b28db073355257aed0180bbb90389cb41fc4248c597ba3e7c8041b91a12883f8",
            // v33（2026-10-07 16:20）：§1 段清单加 ENVELOPE / RULING-ANSWER（信封与裁定随派发物到 worker）。
            ["33"] = "d1de10dedd258bdb42726a2461e63a247934369b26c42dd85a4dec84b4069fe6",
            // v34（2026-10-07 16:50）：§5 回执形状 + 例子改 XML 元素。
            ["34"] = "e8599307615bb726ad51f040a242faeab66fe167995f43e03b1f6b3ed824715b",
            // v35（2026-10-07 17:15）：§5 补「交付键是成员、不许塞进 <answer>」。
            ["35"] = "b64b04aa56034ff11f49fde1239fdd0ee742b443fb6cb4f8ca3ce79a21e2df93",
            // v36（2026-10-07 17:50）：共用段 [FEEDBACK] 补「同一缺口重复 ⇒ 升级为实错」。
            ["36"] = "2e267b83fe61e62779fdda861084a3f6ef535cb6403230d3673a8a993fa4572d",
            // v37（2026-10-07 22:20）：共用段 [DUTY] 补读数时效（与主协议 v52 同一份）。
            ["37"] = "6297e1b26b9e679798105443aa0de25dcba6887663b76e5cb6e53cf9328454d8",
            // v38（2026-10-08 20:45）：共用段 side_effects 的唯一合法值按 kind 渲染（master 给 EXEC 写 none ⇒ 白走一轮）。
            ["38"] = "d4a01205d60a0682925e92a6bc28ba16efe849f9c26e69e3db120835363795b4",
            // v39（2026-10-09）：共用段新增 [PACE] 节拍段。
            ["39"] = "adb00789845d85eeaf16a025c4e3a14595c72ffb690dfb6fa5be78616fd30f5d",
            // v40（2026-10-09）：[PACE] 段补 master 设上限那条线。
            ["40"] = "54ef8b5071a75f2cd678c05bacada5708d89647b2fa285bb2d0a706287bc9abb",
            // v41（2026-10-09 13:10）：共用段 [PACE] 段改「按次派发闸门」+ `[PACE] gate=<秒>`。
            ["41"] = "8a08ba34779687804ba1a4c33a2705e174224fbb70941824a23165ea5f036e02",
            // v42（2026-10-09 13:15）：第 17 条（停/超时/被取代 ⇒ 停下、不续做）+ 第 13 条改「白板只属本派发」。
            ["42"] = "60a91742e5dc1698cac75277d8fa6d859b84819070c34c711c9be68e3f875226",
            // v43（2026-10-09 22:45）：第 16 条的 L3/L4 地址改指**真实位置**（`knowledge/.derived/l3.jsonl` + `knowledge/knowledge.md`）——
            // 旧文指向 `tools/skill-repo/<skill>/L3.jsonl`（SKILL 概念已退役 2026-10-09 ⇒ 那是**死路**）。
            ["43"] = "edd86c95eaff9a5987346c99bf26035e27530e3a2d730bd0fa28a8fddeb8c9e9",
            // v44（2026-10-10）：第 16 条改教「**L3 = 分类总目 + 路由**（l3.jsonl）/ **L4 = 原 SKILL 逐字**（l4.jsonl，L4-<短名>-001）」。
            ["44"] = "f64a546024c8bb40c947ff37e978270aff25706c7a1656068400ff3cf3aca3cc",
            // v45（2026-10-12 · A4 尾）：派生层 JSON→XML —— 契约里点的模型可见路径改 `.derived/l3.xml` / `l4.xml`。
            ["45"] = "53c33ab8b0dd749d3262b71e3bc223da46af1e343fde0808c8cc9755a7290ed0",
            // v46（2026-10-14）：主人令「最长允许的闸门时间 → 1200s」—— `[PACE] ceiling` 白名单加 `1200`。
            ["46"] = "fdbdd9dbf6270056abecc44f8c78f23479e3bed5c41bcb368f11ae3f6b106f34",
            // v47（2026-10-15 13:2x）：ToolFaceClause.Shape 去「旧行式/JSON 仍会被归一」那句（改成直接说不许写 JSON）⇒ 正文变。
            ["47"] = "71e5f468a42ce9b7ee6b9c89e05d43bd131fb1a9dc055265916a5c21f41242ef",
            ["15"] = "fc0cda634abe76a0779602704b4d94287130f394ae19e894cb397fa1a2d62d63",
            ["13"] = "eca820b5760203da7c0070ff51aebef2f49d3aff0db45d25604a12fbd7c3a18c",
        };

    [Fact]
    public void 配置fail_closed_启用但缺baseUrl或model则报错()
    {
        Assert.Throws<InvalidDataException>(() => new WorkerConfiguration { Enabled = true }.Validate());
        Assert.Throws<InvalidDataException>(() =>
            new WorkerConfiguration { Enabled = true, Provider = new WorkerProviderConfiguration { BaseUrl = "x" } }.Validate());

        // 关掉时怎么填都不报（W1：关掉即无副作用）
        new WorkerConfiguration { Enabled = false }.Validate();
    }

    [Fact]
    public void 写类工具默认开放_与主人20260925的裁决一致()
    {
        var cfg = new WorkerConfiguration();
        Assert.Contains("write", cfg.Tools);
        Assert.Contains("edit", cfg.Tools);
        Assert.Contains("read", cfg.Tools);
    }

    [Fact]
    public void 解析_契约块四字段()
    {
        var p = WorkerReportParser.Parse("废话\n[WORKER] conclusion: 结论 | evidence: a.cs:1, b.cs:2 | changes: c.cs | open: none");
        Assert.True(p.Found);
        Assert.Equal("结论", p.Conclusion);
        Assert.Equal(2, p.Evidence.Count);
        Assert.Single(p.Changes);
        Assert.Empty(p.Open);                              // none ⇒ 空清单，不是一条名为 none 的记录

        Assert.False(WorkerReportParser.Parse("没有块").Found);
        Assert.False(WorkerReportParser.Parse("[WORKER]").Found);   // 有标记无字段 ⇒ 仍算没结算
    }

    [Fact]
    public void 解析_裸读数行不得截断契约块()
    {
        // 2026-09-30 常规检测料条目 13（receipt-shape）· 真机：master 自创回执形状（conclusion 首行是路径，
        // 其后是**裸读数行** `bytes=33`）⇒ 旧解析器卡在裸行 ⇒ 其后的 answer/evidence/changes/covered/open
        // 全被静默丢掉 ⇒ `covered` 缺 ⇒ 应有集合闸门误判 `covered=(未报)`。
        var p = WorkerReportParser.Parse("""
            [WORKER]
            conclusion: /abs/verify.txt
            bytes=33
            lines=3
            sha256=d12166ba
            answer: 已就位
            evidence: shasum rc=0
            changes: none
            covered: full
            open: none
            """);
        Assert.True(p.Found);
        Assert.Equal("/abs/verify.txt", p.Conclusion);
        Assert.Equal("已就位", p.Answer);
        Assert.Equal("full", p.Covered);            // 裸行之后的字段**必须**仍在
        Assert.Empty(p.Open);
        Assert.Contains("bytes=33", p.Evidence);      // 裸读数是读数 ⇒ 归入证据，不许丢
        Assert.Contains("sha256=d12166ba", p.Evidence);

        // 散文行仍是**块结束**（不能因为它不像字段就无限把后面的正文当字段扫）。
        var q = WorkerReportParser.Parse("[WORKER] conclusion: done\n这一行是散文\ncovered: full");
        Assert.True(q.Found);
        Assert.Equal(string.Empty, q.Covered);
    }

    [Fact]
    public void 被拒_已被后续成功结果补救_不算未补救()
    {
        // 2026-09-30 待办 1：旧判据「本跑出现过 ToolDenied」⇒ 拒了又换法做成也被判失败（真机）。
        static SessionEvent E(int seq, SessionEventKind k, string t) => new(seq, k, t, null, $"E{seq:000}");

        var remedied = new List<SessionEvent>
        {
            E(1, SessionEventKind.ToolDenied, "exec command: stat -f … → 拒"),
            E(2, SessionEventKind.ToolResult, "exec shasum -a 256 … → exit 0 | --- stdout --- | d12166ba"),
        };
        Assert.False(MinimalWorker.UnremediedDenial(remedied));

        var unremedied = new List<SessionEvent>
        {
            E(1, SessionEventKind.ToolResult, "exec ls -la … → exit 0"),
            E(2, SessionEventKind.ToolDenied, "exec command: cat … → 拒"),
        };
        Assert.True(MinimalWorker.UnremediedDenial(unremedied));

        Assert.False(MinimalWorker.UnremediedDenial([]));   // 本跑没被拒 ⇒ 不算
    }


}
