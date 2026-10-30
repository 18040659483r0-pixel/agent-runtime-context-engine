namespace AgentRuntime.Presentation;

/// <summary>TUI 的界面语言（**与「跟用户说话 / 跟跟远端模型说话」的语言无关**）。</summary>
public enum TuiLanguage
{
    /// <summary>默认：英文。</summary>
    En = 0,

    /// <summary>中文。</summary>
    Zh = 1,

    /// <summary>日本語。</summary>
    Ja = 2,
}

/// <summary>
/// **界面文案的强类型键**（2026-10-29 主人令）：写错键名 = 编译不过，杜绝「漏翻 / 拼错键」。
/// <para>新增文案：先在这里加一个枚举值，再到 <see cref="TuiStrings"/> 的表里补三语。</para>
/// </summary>
public enum TuiKey
{
    Title,
    Session,
    Stack,
    ContextModules,
    Cached,
    Active,
    RuntimeOnly,
    More,
    Modules,
    ThisTask,
    ThisTaskNone,
    CacheHit,
    CacheHitNone,
    ContextTotal,
    ContextTotalEst,
    Board,
    Draft,
    Focus,
    Lines,
    Tags,
    FocusNone,
    Unavailable,
    StateReady,
    StateRunning,
    StateExecuting,
    TabHint,
    Thinking,
    Think,
    Intent,
    Result,
    InProgress,
    ReportTitle,
    Lifecycle,
    Elapsed,
    ToolResults,
    Ran,
    ExitCode,
    ReadFile,
    WroteFile,
    EditedFile,
    LookedUp,
    ListedDir,
    ChangedPaths,
    EventsLabel,
    Meter,
    SelectionPicked,
    SelectionHint,
    SessionConfig,
    SessionReset,
    InterruptCommand,
    ErrorPrefix,
    StartupNoKey,
    Prompt,
    SubmitHint,
    InterruptAgain,
    TurnCancelled,
    TurnCancelledContinue,
    ResumeSection,

    // —— 启动播报（SessionLifecycle：StartReport / StartAtBoot）——
    StartSession,
    StartStreamMemory,
    StartStreamPath,
    StartNoSnapshot,
    StartHandover,
    StartPrevVolume,
    StartClean,
    StartLoaded,
    StartWorkers,
    StartNoWorkers,
    StartEarlier,
    AutoStartNote,
    AutoStartFailed,

    // —— 顶栏 ——
    EmptyZero,
    NextTurn,

    // —— 卡片：由工具调用合成的意图短语 ——
    ActRead,
    ActList,
    ActWrite,
    ActEdit,
    ActIndex,
    ActExec,
    ActFetch,
    ActSearch,
    ActHttp,
    ActScope,
    IntentJoin,
    FileWord,
    UnknownWord,
    DeniedCount,
    PlanFallback,

    // —— 输入层 ——
    PasteNotice,
    PasteDisplay,
    ResumePrefix,
    StoppedSuffix,
}

/// <summary>
/// **界面固定文字的单一来源**（默认英文；另有中文 / 日本語）。
/// <para>只管界面文案，不管模型输出；与对话语言无关。</para>
/// <para>选择顺序：环境变量 <c>WB_TUI_LANG=en|zh|ja</c> ⇒ 默认 <see cref="TuiLanguage.En"/>。</para>
/// </summary>
public static class TuiStrings
{
    /// <summary>当前语言（静态，全进程一份）。</summary>
    public static TuiLanguage Language { get; set; } = Resolve();

    /// <summary>
    /// **没设 <c>WB_TUI_LANG</c> 时用哪一档**：设了环境变量就以环境变量为准，否则用调用方给的默认。
    /// <para>2026-10-29 TUI1 退役后本进程只有 TUI2（默认英文）；本方法留给以后的宿主声明自己的台面语言。</para>
    /// </summary>
    public static void ApplyDefault(TuiLanguage fallback)
    {
        var raw = (Environment.GetEnvironmentVariable("WB_TUI_LANG") ?? string.Empty).Trim();
        Language = raw.Length == 0 ? fallback : Resolve();
    }

    private static TuiLanguage Resolve()
    {
        var raw = (Environment.GetEnvironmentVariable("WB_TUI_LANG") ?? string.Empty).Trim();
        return raw.ToLowerInvariant() switch
        {
            "zh" or "cn" or "zh-cn" => TuiLanguage.Zh,
            "ja" or "jp" or "ja-jp" => TuiLanguage.Ja,
            _ => TuiLanguage.En,
        };
    }

    /// <summary>取一条文案。</summary>
    public static string T(TuiKey key)
    {
        var v = Table[key];
        return Language switch
        {
            TuiLanguage.Zh => v.Zh,
            TuiLanguage.Ja => v.Ja,
            _ => v.En,
        };
    }

    /// <summary>带格式参数的文案。</summary>
    public static string F(TuiKey key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, T(key), args);

    /// <summary>全部键（自检/测试用）。</summary>
    public static IReadOnlyCollection<TuiKey> Keys => Table.Keys;

    private static readonly Dictionary<TuiKey, (string En, string Zh, string Ja)> Table = new()
    {
        [TuiKey.Title] = ("ⓘ AgentRuntime TUI2 - WhiteBox", "ⓘ AgentRuntime TUI2 - WhiteBox", "ⓘ AgentRuntime TUI2 - WhiteBox"),
        [TuiKey.Session] = ("Session", "会话", "セッション"),
        [TuiKey.Stack] = ("Stack", "区栈", "スタック"),
        [TuiKey.ContextModules] = ("Modules", "模块", "モジュール"),
        [TuiKey.Cached] = ("(Cached)", "(Cached)", "(Cached)"),
        [TuiKey.Active] = ("(Active)", "(Active)", "(Active)"),
        [TuiKey.RuntimeOnly] = ("(Runtime)", "(Runtime)", "(Runtime)"),
        [TuiKey.More] = ("… (+{0})", "…（另 {0} 个）", "…（他 {0}）"),
        [TuiKey.Modules] = ("{0} modules", "{0} 模块", "{0} モジュール"),
        [TuiKey.ThisTask] = ("this task {0} tok", "本 task 写入 {0} tok", "このタスク {0} tok"),
        [TuiKey.ThisTaskNone] = ("this task — tok", "本 task 写入 — tok", "このタスク — tok"),
        [TuiKey.CacheHit] = ("cache hit {0}", "缓存命中 {0}", "キャッシュヒット {0}"),
        [TuiKey.CacheHitNone] = ("cache hit —", "缓存命中 —", "キャッシュヒット —"),
        [TuiKey.ContextTotal] = ("context total {0} tok", "当前上下文合计 {0} tok", "コンテキスト合計 {0} tok"),
        [TuiKey.ContextTotalEst] = ("context total ~{0} tok (est.)", "当前上下文合计 ~{0} tok（预估）", "コンテキスト合計 ~{0} tok（推定）"),
        [TuiKey.Board] = ("Board", "白板", "ホワイトボード"),
        [TuiKey.Draft] = ("Draft", "草稿", "下書き"),
        [TuiKey.Focus] = ("Focus", "焦点", "フォーカス"),
        [TuiKey.Lines] = ("{0} lines", "{0} 行", "{0} 行"),
        [TuiKey.Tags] = ("{0} tags", "{0} 个标签", "{0} タグ"),
        [TuiKey.FocusNone] = ("(model reported no [FOCUS])", "（模型本场未报 [FOCUS]）", "（モデルは [FOCUS] 未報告）"),
        [TuiKey.Unavailable] = ("metrics unavailable", "读数暂不可用", "读数を取得できません"),
        [TuiKey.StateReady] = ("ready", "就绪", "準備完了"),
        [TuiKey.StateRunning] = ("running", "运行中", "実行中"),
        [TuiKey.StateExecuting] = ("executing", "执行中", "コマンド実行中"),
        [TuiKey.TabHint] = ("Tab: modules · Ctrl+C twice: quit", "Tab 切模块区 · Ctrl+C 两下退出", "Tab: モジュール · Ctrl+C 2回で終了"),
        [TuiKey.Thinking] = ("model thinking… {0}", "远端模型正在思考… {0}", "モデルが考えています… {0}"),
        [TuiKey.Think] = ("think {0}", "think {0}", "think {0}"),
        [TuiKey.Intent] = ("intent: ", "意图：", "意図："),
        [TuiKey.Result] = ("result: ", "结果：", "結果："),
        [TuiKey.InProgress] = ("(running)", "（进行中）", "（進行中）"),
        [TuiKey.ReportTitle] = ("Decision report #{0}", "决策报告 #{0}", "意思決定レポート #{0}"),
        [TuiKey.Lifecycle] = ("Lifecycle #{0}", "生命周期 #{0}", "ライフサイクル #{0}"),
        [TuiKey.Elapsed] = ("· {0}", "· 用时 {0}", "· {0}"),
        [TuiKey.ToolResults] = ("{0} tool results; last: {1}", "{0} 条工具结果；最后：{1}", "{0} 件のツール結果；最後：{1}"),
        [TuiKey.Ran] = ("ran {0}", "已运行 {0}", "実行 {0}"),
        [TuiKey.ExitCode] = ("(exit {0})", "（exit {0}）", "（exit {0}）"),
        [TuiKey.ReadFile] = ("read file ({0} chars)", "已读取文件（{0} 字）", "ファイル読取（{0} 文字）"),
        [TuiKey.WroteFile] = ("wrote file", "已写入文件", "ファイル書込"),
        [TuiKey.EditedFile] = ("edited file", "已修改文件", "ファイル編集"),
        [TuiKey.LookedUp] = ("looked up symbol", "已取到定义", "シンボル参照"),
        [TuiKey.ListedDir] = ("listed dir ({0} chars)", "已列出目录（{0} 字）", "ディレクトリ一覧（{0} 文字）"),
        [TuiKey.ChangedPaths] = ("{0} paths changed", "改动 {0} 路径", "{0} パス変更"),
        [TuiKey.EventsLabel] = ("events", "事件", "イベント"),
        [TuiKey.Meter] = ("T {0} (S {1})", "T {0}（S {1}）", "T {0}（S {1}）"),
        [TuiKey.SelectionPicked] = ("{0} chars selected · ⌘C copy · Esc cancel", "已选中 {0} 字 · ⌘C 复制 · Esc 取消", "{0} 文字を選択 · ⌘C コピー · Esc 解除"),
        [TuiKey.SelectionHint] = ("drag to select", "拖选正文", "ドラッグで選択"),
        [TuiKey.SessionConfig] = ("[session] config {0}", "[会话] 配置 {0}", "[セッション] 設定 {0}"),
        [TuiKey.SessionReset] = ("[session] closed, waiting for start (/start opens a new session)", "[会话] 已收尾、待 start（/start 开新会话）", "[セッション] 終了済み・start 待ち（/start で新規）"),
        [TuiKey.InterruptCommand] = ("[interrupt] command cancelled.", "[中断] 命令已取消。", "[中断] コマンドを中止しました。"),
        [TuiKey.ErrorPrefix] = ("[error] ", "[错误] ", "[エラー] "),
        [TuiKey.StartupNoKey] = ("startup failed: missing API key", "启动失败：缺少 API Key", "起動失敗：API キーがありません"),
        [TuiKey.Prompt] = ("agent-runtime > ", "agent-runtime > ", "agent-runtime > "),
        [TuiKey.SubmitHint] = ("[session] Enter submits · Ctrl+J/Alt+Enter newline · Ctrl+C twice quits · Ctrl+L redraw", "[会话] Enter 提交 · Ctrl+J/Alt+Enter 换行 · Ctrl+C 两下退出 · Ctrl+L 重画", "[セッション] Enter 送信 · Ctrl+J/Alt+Enter 改行 · Ctrl+C 2回で終了 · Ctrl+L 再描画"),
        [TuiKey.InterruptAgain] = ("[interrupt] press Ctrl+C again to quit (or Ctrl+D).", "[中断] 再按一次 Ctrl+C 退出（Ctrl+D 也可以）。", "[中断] もう一度 Ctrl+C で終了（Ctrl+D も可）。"),
        [TuiKey.TurnCancelledContinue] = (
            "[interrupt] current turn cancelled; say \"continue\" to resume.",
            "[中断] 当前轮已取消；说「继续」可接上。",
            "[中断] 現ターンを中止しました。「続けて」で再開できます。"),
        [TuiKey.TurnCancelled] = ("[interrupt] current turn cancelled.", "[中断] 当前轮已取消。", "[中断] 現在のターンを中止しました。"),
        [TuiKey.ResumeSection] = ("[session] resumed section {0} -- to open another volume: --stream <that stream file>", "[会话] 接上上一节的会话（{0}）—— 想回别的卷：--stream <那次的流文件>", "[セッション] 前のセクション {0} を継続 -- 別の巻を開くには: --stream <その流ファイル>"),

        // 启动播报（SessionLifecycle）——原先写死在 Hosting 里，2026-10-29 主人指出「TUI2 里还有中文」。
        [TuiKey.StartSession] = ("[start] session #{0} started: turns reset, new empty event stream, cache rebuilt; stable prefix unchanged byte for byte.", "[开始] 第 {0} 次 start：新会话已开（轮次清零、事件流空起点、缓存重新建立；稳定前缀一个字节未改）。", "[開始] {0} 回目の start：新セッション開始（ターン清零・空のイベントストリーム・キャッシュ再構築；安定プレフィックスは 1 バイトも変更なし）。"),
        [TuiKey.StartStreamMemory] = ("[start] new event stream: memory only (this session never wrote to disk)", "[开始] 新事件流：只存内存（本会话本来就没落盘）", "[開始] 新イベントストリーム：メモリのみ（このセッションは元々ディスク未保存）"),
        [TuiKey.StartStreamPath] = ("[start] new event stream: {0} (empty start)", "[开始] 新事件流：{0}（空起点）", "[開始] 新イベントストリーム：{0}（空の起点）"),
        [TuiKey.StartNoSnapshot] = ("[start] end state: no snapshot found -> board and draft start from current storage (said plainly, no pretending it was loaded).", "[开始] 末态：**没有找到末态快照** ⇒ 白板与草稿按当前存储开始（说出来，不假装装载过）。", "[開始] 末態：**スナップショットが見つかりません** ⇒ ホワイトボードと下書きは現在のストレージから開始（読み込んだふりはしない）。"),
        [TuiKey.StartHandover] = ("[start] board and draft from end state: {0}", "[开始] 白板与草稿来自末态：{0}", "[開始] ホワイトボードと下書きは末態から：{0}"),
        [TuiKey.StartPrevVolume] = ("[start] previous volume got to: {0} (continue -- no need to restate context)", "[开始] 上一卷做到哪：{0}（**接着做，不用重述上下文**）", "[開始] 前の巻の到達点：{0}（**続行可・文脈の再説明は不要**）"),
        [TuiKey.StartClean] = ("[start] board R4 / draft R5 both empty (previous session closed out) -- this section starts from a clean desk.", "[开始] 白板 R4 / 草稿 R5 均为空（上一会话收尾已销账）—— 这一节从干净台面开始。", "[開始] ホワイトボード R4 / 下書き R5 は空（前セッションは終了済み）—— この節はきれいな状態から。"),
        [TuiKey.StartLoaded] = ("[start] loaded: board R4 {0} lines · draft R5 {1} lines -- just keep going (no need to restate context).", "[开始] 已装载：白板 R4 {0} 行 · 草稿 R5 {1} 行 —— **接着往下做就行**（不用重述上下文）。", "[開始] 読み込み済み：ホワイトボード R4 {0} 行 · 下書き R5 {1} 行 —— **そのまま続行**（文脈の再説明は不要）。"),
        [TuiKey.StartWorkers] = ("[start] workers ({0} in one set, read one by one): {1}", "[开始] worker 侧（{0} 家，同一个集合，逐个读）：{1}", "[開始] worker（{0} 家・同一集合・順に読む）：{1}"),
        [TuiKey.StartNoWorkers] = ("[start] workers: none configured / none dispatched yet", "[开始] worker 侧：没有 worker 家（没配 / 还没派发过）", "[開始] worker：未設定 / まだ派遣なし"),
        [TuiKey.StartEarlier] = ("[start] to open an earlier session: --stream <that stream file> (old streams kept in place).", "[开始] 想回到更早的会话：--stream <那次的流文件>（旧流全部原地保留）。", "[開始] 以前のセッションを開くには：--stream <そのストリームファイル>（旧ストリームはそのまま保存）。"),
        [TuiKey.AutoStartNote] = ("[session] previous session was closed without /start -> auto-start (picks up even when nobody is around):", "[会话] 上次已收尾、未 start ⇒ **自动 start**（人不在场也接得上）：", "[セッション] 前回は終了済み・start 未実行 ⇒ **自動 start**（不在でも引き継げる）："),
        [TuiKey.AutoStartFailed] = ("[session] ⚠️ auto-start failed (continuing as-is, not pretending success): {0}", "[会话] ⚠️ 自动 start 失败（按原样继续，不假装成功）：{0}", "[セッション] ⚠️ 自動 start 失敗（そのまま続行・成功のふりはしない）：{0}"),

        [TuiKey.EmptyZero] = ("(empty, zero injection)", "（空，零注入）", "（空・ゼロ注入）"),
        [TuiKey.NextTurn] = ("(next turn)", "（下一轮）", "（次のターン）"),

        [TuiKey.ActRead] = ("read {0}", "读取 {0}", "読取 {0}"),
        [TuiKey.ActList] = ("list {0}", "列出 {0}", "一覧 {0}"),
        [TuiKey.ActWrite] = ("write {0}", "写入 {0}", "書込 {0}"),
        [TuiKey.ActEdit] = ("edit {0}", "修改 {0}", "編集 {0}"),
        [TuiKey.ActIndex] = ("look up {0}", "检索符号 {0}", "シンボル検索 {0}"),
        [TuiKey.ActExec] = ("run {0}", "运行 {0}", "実行 {0}"),
        [TuiKey.ActFetch] = ("fetch {0}", "抓取 {0}", "取得 {0}"),
        [TuiKey.ActSearch] = ("search {0}", "检索 {0}", "検索 {0}"),
        [TuiKey.ActHttp] = ("HTTP request {0}", "发 HTTP 请求 {0}", "HTTP リクエスト {0}"),
        [TuiKey.ActScope] = ("locate task scope", "定位任务范围", "タスク範囲を特定"),
        [TuiKey.IntentJoin] = (", ", "，", "、"),
        [TuiKey.FileWord] = ("file", "文件", "ファイル"),
        [TuiKey.UnknownWord] = ("(unknown)", "（未知）", "（不明）"),
        [TuiKey.DeniedCount] = ("{0} denied", "被拒 {0}", "拒否 {0}"),
        [TuiKey.PlanFallback] = ("summarise results and answer", "汇总结果并作答", "結果を整理して回答"),

        [TuiKey.PasteNotice] = ("[paste] folded into 1 atomic block ({0} chars / {1} lines)", "[粘贴] 已收成 1 个原子块（{0} 字符 / {1} 行）", "[貼付] 1 つの原子ブロックに集約（{0} 文字 / {1} 行）"),
        [TuiKey.PasteDisplay] = ("[paste {0} lines / {1} chars]", "[粘贴 {0} 行 / {1} 字符]", "[貼付 {0} 行 / {1} 文字]"),
        [TuiKey.ResumePrefix] = ("[resume] ", "[续] ", "[継続] "),
        [TuiKey.StoppedSuffix] = (" (stopped)", "（已停）", "（停止）"),
    };
}
