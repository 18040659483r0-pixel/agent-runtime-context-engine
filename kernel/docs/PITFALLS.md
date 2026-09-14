# AgentRuntime 踩坑集（L3 最细颗粒度，素材层）

> 通用方法论查 `workspace/METHODOLOGY.md`（L1/L2）；本文件只记本项目具体坑。

## 1. dotnet 不在 PATH（2026-09-14，Mac）

- **现象**：`which dotnet` 找不到，但 SDK 已装在 `~/.dotnet`（10.0.400，2026-08-18 装的）。
- **处理**：① 软链 `ln -sf ~/.dotnet/dotnet ~/.local/bin/dotnet`；② 在 `~/.zshrc` 追加 `export PATH="$HOME/.local/bin:$PATH"`（带注释，可随时删）。
- **⚠️ 关键坑**：**光做软链不够**。`~/.local/bin` 只在 OpenClaw 网关进程的环境里存在，**新开终端的登录 shell 里没有** —— 用 `env -i HOME=$HOME /bin/zsh -lic 'command -v dotnet'` 才能验证真相（普通 `zsh -lic` 会继承父进程 PATH，给出假阳性）。
- **没做**：没装 brew cask（需 sudo、且会再装一份 SDK）；`/usr/local/bin` 属 root 不可写。
- **跨端注意**：本解决方案**刻意不写 `global.json`**，避免两端 SDK 补丁号不同导致 `dotnet build` 直接失败；Windows 侧用 `dotnet --list-sdks` 确认 10.x 即可。

## 1.5 xUnit v2 → v3 迁移（2026-09-14）

- **版本选择**：`xunit.v3` **3.2.2**（最新 v3 稳定）+ `xunit.runner.visualstudio` **3.1.5**（v3 线）+ `Microsoft.NET.Test.Sdk` **18.10.0**；**不上 v4.x**（主人定）。
- **csproj 必改**：加 `<OutputType>Exe</OutputType>` —— v3 的测试工程是**可执行程序**（自带 runner），不是库。
- **分析器新规则 xUnit1051**：所有接受 `CancellationToken` 的调用都要传 `TestContext.Current.CancellationToken`，否则 build 出警告；**故意传自定义 token 的用例**（如“已取消的 token 会向上传播”）用 `#pragma warning disable xUnit1051` 包住并注明原因——不要为了消警告把测试意图改掉。

## 2. `System.Text.Json` 默认会把中文转义成 `\uXXXX`（2026-09-14）

- **现象**：测试断言请求体含 `"content":"你好"` 失败 —— 实际发出的是 `"content":"\u4F60\u597D"`。
- **两层处理**：
  - **线格式**：写请求时用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，中文原样发 UTF-8。理由：① 字节更少；② 上游分词更贴近自然中文（转义序列会把一个汉字拆成多个 token）→ 直接影响 Benchmark 的 Token 指标。
  - **测试**：不要用「原始 JSON 字符串包含中文」断言，改为 `JsonDocument` 解析后断言值；原始串断言只用于 ASCII（键名、字段存在性）。
- **反例（不要做）**：为了让字符串断言通过而关掉转义、却忘了这是**协议行为**——它是有意决策，必须有测试钉住（`中文原样发UTF8_不转义为unicode序列`）。

## 3. `HttpResponseMessage` 的 Content 读取与析构（2026-09-14）

- Provider 里读响应体必须用 `await content.ReadAsStringAsync(ct)`（带取消），并在 `using (httpResponse)` 内完成；
  否则取消时可能拿到半截 body 或被提前 Dispose。

## 4. 测试要「零网络」才能当尺子的地基（2026-09-14）

- Runtime 测试用 `FakeModelClient`；Provider 测试用 `StubHttpMessageHandler` 拦请求，断言 URL / 头 / body。
- 真实调用只走 CLI + mock provider（`tools/mock-provider/`）或真实端点，**不进单元测试**——否则测试受网络与费用影响，不再是确定性证据。

## 5. OpenAI 兼容端点的 baseUrl 语义（2026-09-14）

- DeepSeek 官方：`baseUrl = https://api.deepseek.com` **不带 `/v1` 也能通**（拼出 `/chat/completions`）；DeepInfra 则要带 `/v1/openai`。
- **不要自动补 `/v1`**：OpenClaw 侧已被这个坑咬过（带不带 `/v1` 语义不同、拼错就 404）。坚持「baseUrl 写什么就拼什么」，只做去尾斜杠。

## 6. 模块管线：用户消息只能由引擎追加（2026-09-14）

- **规矩**：模块的 `ContributeAsync` 只允许贡献「历史 / 规则 / 知识」类消息；**本轮用户消息由引擎统一 add 在最后**。
- **为什么**：如果让模块自己断言用户消息位置，一旦增减模块，消息顺序就会漂移 —— 消融实验立刻失效（对比的就不是「只差一个模块」了）。
- `SessionModule.ObserveAsync` 靠「最后一条即本轮用户输入」这个不变量取用户文本 —— 这个不变量由引擎保证，不要在模块里重复实现。

## 7. 消融测试怎么写才真的能守（2026-09-14）

- **不要**只断言「带 A+B 时消息数 = 3」。要断言：**去掉 A 后，剩余的贡献逐字相同**：
  ```csharp
  var expected = withBoth.Request.Messages.Where(m => m != rules).Select(m => (m.Role, m.Content)).ToArray();
  Assert.Equal(expected, withOne.Request.Messages.Select(m => (m.Role, m.Content)).ToArray());
  ```
- `ChatMessage` 是 **class 不是 record**，`m != rules` 是**引用比较** —— 这里正好是我们要的语义（“就是那一条实例”），不要改成值相等。

## 8. 上游前缀缓存按 64-token 块对齐（2026-09-14 实测）

- **实测**：同一 1810-token 前缀，第 1 次 `cache_hit=0`，第 2 次 `cache_hit=1664`（= **64 × 26**）→ 命中按 64 token 块计算；短前缀（几十 token）**永远 miss**。
- **别误判**：`cached = 0` ≠ 上游不支持缓存，很可能只是**前缀太短 / 前缀不稳定**。先用 T02 长前缀探针确认，再下结论。
- **对架构的含义**：想让缓存真省钱，必须把「稳定前缀」堆长且逐字节不动（→ V2 Frozen Context / V3 Append Stream）；动态内容一律往后放。
- 后续 55 次调用的阶梯实验再次印证：所有命中量都是 64 的整数倍（896/9856/256）。

## 9. 变体隔离：salt 必须放「冻结前缀最前面」（2026-09-14 自己踩的坑）

- **错误写法**：`$\"{tierText}\\n<<< end · {salt} >>>\"` —— salt 在尾部，前缀主体仍相同。
  后果：校准调用与前面的变体**已经把这个前缀烘热了** → 后面变体第 1 轮就直接命中（L2 实测 t1 命中 9856），**看起来像“运行时自动吃到缓存”，实际是实验污染**。
- **正确写法**：`$\"«{salt}»\\n{tierText}\"` —— salt 在最前，不同变体/档位**连一个字节的公共前缀都没有**。
- **自检手段**：每个变体**第 1 轮必须 cached = 0**（冷启动）。不为 0 就说明还有外部预热，本次数据作废。
- **为何容易忽略**：污染不会报错，只会让数字**变好看**。这类“沉默的乐观偏差”必须用纪律拦，不能靠眼看。
