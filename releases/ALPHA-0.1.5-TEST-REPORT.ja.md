# Alpha 0.1.5 · テストおよびリリースレポート（v9.0）

> English: [`ALPHA-0.1.5-TEST-REPORT.md`](ALPHA-0.1.5-TEST-REPORT.md) · 中文: [`ALPHA-0.1.5-TEST-REPORT.zh-CN.md`](ALPHA-0.1.5-TEST-REPORT.zh-CN.md) · リリースノート: [`ALPHA-0.1.5-RELEASE.ja.md`](ALPHA-0.1.5-RELEASE.ja.md)

> すべての数値は**公開スライス内**（新規ディレクトリ）で取得しています。プライベートなビルドツリーでは測っていません。

| 項目 | 数値 |
|---|---|
| ビルド（`AgentRuntime.slnx -c Release`） | **エラー 0** |
| テスト（`tests/AgentRuntime.Tests`） | **1347 / 1349 成功** |
| 失敗 2 件 | `PitfallsDerivedTests.公開変体_とpython派生物が逐バイト一致` · `坑集索引_とpython派生物が逐バイト一致` — **内部専用**：内部の落とし穴コーパスの派生物が必要で、公開スライスには含まれません |
| TUI2 セルフテスト（`AgentRuntime.Tui2 --selftest`） | **PASS（全アサーション）** |
| 公開前スキャン（`wb-cli scan <slice>`） | **ハード違反 0**、公開可（ソフトは敬称・人名のみ） |
| プロトコル領域 | v102（25 行 / 5,234 token ≤ 5,600） |
| スライス規模 | **635 ファイル / 1.6 MB**（tar、`bin/obj` なし） |

## 再現（誰でも実行できます）

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## 本版のテスト変更

**追加**
- `TuiStringsTests`：3 言語のキー集合が一対一、**英語時に漢字なし**、起動告知が言語パックを通ること（中国語に切り替えれば戻る）
- `TopBandLanguageTests`：実ホストでトップバンドを描画し、英語時に漢字が一文字でも出たら赤。空の台は言語パックの空文言であること
- `KnowledgeSinkTests`：**規格外のノードマーカーはエラーにし、黙って飛ばさない**（正規形の正例つき）
- `TuiInvariantTests`：**事前組み立てしたリクエストを同一オブジェクトのまま送る（1 ターン 1 回組み立て）**（`Assert.Same`）

**TUI1 退役に伴い削除**（12 ファイル＋フレーム単位のケース）
- `TuiSplitTests`、`TuiInvariantTests` のフレーム単位ケース、`TuiExitVoiceTests`、`BracketedPasteTests`、
  `ExitPolicyTests`、`InputCommandRuleTests`、`LifecycleCardViewTests`、`SelfReportDisplayTests`、
  `DecisionReportBindingTests`、`DecisionReportFlowTests`、`ContinuationCapTests`、`LifecycleCloseoutTests`、
  `ThinkingLineRoleTests`、`ToolVisibilityTests`

> 内部ビルドの全件：**1349 / 1349**。公開スライスは内部専用の 2 件だけ少なくなります。
