# Alpha 0.1.6 · テストおよびリリースレポート（v9.1）

> English: [`ALPHA-0.1.6-TEST-REPORT.md`](ALPHA-0.1.6-TEST-REPORT.md) · 中文: [`ALPHA-0.1.6-TEST-REPORT.zh-CN.md`](ALPHA-0.1.6-TEST-REPORT.zh-CN.md) · リリースノート: [`ALPHA-0.1.6-RELEASE.ja.md`](ALPHA-0.1.6-RELEASE.ja.md)

数値はすべて**公開スライス内**、ビルド成果物を削除した後に取得しています。

| 項目 | 値 |
|---|---|
| ビルド | **エラー 0** |
| テスト | **1359 / 1361** |
| 失敗 2 件 | `PitfallsDerivedTests` — **内部専用**（内部コーパスが必要） |
| 公開前スキャン | **ハード違反 0** |
| プロトコル | v104 · 26 行 / 22,196 文字 / 5,549 token（≤5,600） |
| 規模 | **655 ファイル / tar 1.6 MB**（bin/obj なし） |
| 配備コーパス | ガイド 5 文書 + 4 ディレクトリ + 知識シード 19/19/19/19 + 派生層 |

## 再現

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```
