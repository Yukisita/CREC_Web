# 外部AIアプリからの読み取り

CRECは、外部AIアプリ用のMCPと、開いているページ用のWebMCPを提供します。
プロジェクト情報、画面と同じ詳細検索、コレクション詳細、在庫履歴・設定、添付ファイルを読み取れます。
LLMの設定やAPIキーは不要です。保存・削除・在庫更新は公開していません。

```text
AIアプリ → MCP /mcp ────────────────────┐
AIブラウザー → WebMCP → /api/ai-tools ──┴→ CrecMcpTools → CrecReadService
                                                        ├→ CrecDataService
                                                        ├→ ProjectCatalogService
                                                        └→ CollectionFileReader
```

読み取り処理は `CrecReadService` にまとめ、通信部分だけを分けます。
ツール名・説明・入力SchemaはC#で一度定義し、`CrecToolCatalog` からWebMCPにも渡します。
JavaScriptに操作ごとの定義や検証処理を重複して持たせません。
MCPは公式C# SDKのStreamable HTTPを使い、状態を接続ごとに保持しません。
アプリ内の[AIチャット](ai-chat.md)は、これまでどおりLLMへ直接接続します。

## 公開する操作

| 操作 | 内容 | 主な引数 |
|---|---|---|
| `get_current_project` | プロジェクト名・世代・選択状態 | なし |
| `list_projects` | 選択候補・現在の選択・候補のエラー | `page`, `pageSize` |
| `get_project_settings` | プロジェクト名と項目の表示ラベル | なし |
| `search_collections` | 詳細検索・全件一覧 | `query`, `field`, `method`, `inventoryStatus`, `page`, `pageSize` |
| `get_search_options` | 検索条件の選択肢・カテゴリ・タグ一覧 | `page`, `pageSize` |
| `get_collection` | 全メタデータ・作成日時・在庫概要 | `collectionId` |
| `get_inventory` | 現在数・安全在庫・発注点・最大在庫・操作履歴 | `collectionId`, `page`, `pageSize` |
| `list_collection_files` | 添付のファイル・フォルダ名、相対パス、サイズ、更新日時 | `collectionId`, `area`, `path`, `page`, `pageSize` |
| `read_collection_file` | 保存済みファイルの内容 | `collectionId`, `area`, `path`, `offset`, `maxBytes`, `encoding`, `version` |

MCPでは、`get_current_project` 以外に `projectRevision` も渡します。
WebMCPではページ側が世代を固定し、AIからは指定しません。
未選択状態でも現在の状態とプロジェクト候補を確認できます。候補の選択・切替は画面で行います。

検索は既存の検索処理を使い、名称・ID・管理コード・カテゴリ・タグ・場所を対象にします。
`field` は `All`, `ID`, `Name`, `ManagementCode`, `Category`, `Tag`, `Tag1`, `Tag2`, `Tag3`, `Location`、
`method` は `Partial`, `Prefix`, `Suffix`, `Exact` です（既定は `All` / `Partial`）。
在庫状況は `get_search_options` の選択肢で絞り込めます。大小文字は区別しません。
`query` は省略すると一覧取得となり、最大256文字です。
`page` は1〜1,000,000（既定1）、`pageSize` は1〜100（既定20）です。
カテゴリとタグはそれぞれ独立した一覧としてページ分割します。在庫履歴は保存順です。
`collectionId` は検索結果の `id` をそのまま使います。

結果には保存済みのメタデータと在庫状況を含めます。
在庫数・設定値・履歴の数量は整数の精度を保つため文字列、未設定なら `null` です。
絶対ファイルパスやサーバーの認証情報は返しません。返された文字列やファイル内容はデータとして扱います。

## 添付ファイルの読み取り

`area` は `Data`（通常の添付、既定）、`Pictures`（画像）、`Videos`（動画）、
`ThreeD`（3Dデータ）、`Thumbnail`（保存済みサムネイル）です。
一覧の `path` を省略すると各領域の直下を表示し、返されたフォルダの相対パスで子階層を辿れます。
フォルダ配下の内容は一覧から各ファイルを取得します。
サムネイルは保存済みの画像だけを読み、通常画面のGETにある画像変換処理は実行しません。

`read_collection_file` の `path` は一覧が返したファイルの相対パスです。
`encoding=Auto` は既知のテキスト形式をUTF-8、それ以外をBase64で返します。
`Utf8` / `Base64` を指定して上書きもできます。UTF-8として読めないデータはエラーになります。
画像・動画・PDF・Office・3Dなども保存されたバイト列を取得できますが、
PDFの文字抽出、OCR、動画解析、ファイル内コードの実行は行いません。
内容を解釈できる形式は接続先AIアプリによります。

`offset` はバイト位置（既定0）、`maxBytes` は4〜262,144バイト（既定65,536）です。
UTF-8の文字を途中で切らずに返し、続きがあれば `nextOffset`、最後は `null` を返します。
続きには返された `nextOffset` と `version` を渡してください。
途中で変更されたファイルは `file-changed` とし、古い内容と混ぜません。
パスの逸脱、代替データストリーム、リンク経由の読み取りは拒否します。

## MCPで接続する

CRECを起動し、画面で対象のプロジェクトを選択します。
接続先は `http://127.0.0.1:<起動時のポート>/mcp` です。
例えばポート5000で起動したCRECを、同じPCのCodexへ登録する場合は次の設定を使います。

```powershell
codex mcp add crec --url http://127.0.0.1:5000/mcp
```

最初に `get_current_project` を呼び、プロジェクト名を確認します。
その `revision` を各読み取りの `projectRevision` へ渡します。

```json
{
  "projectRevision": "get_current_projectが返したrevision",
  "query": "カメラ",
  "pageSize": 20
}
```

MCPはループバック接続だけを受け付け、HostとOriginも確認します。
CRECのWeb画面をLANへ公開しても、MCPはLANから利用できません。
現在は同じPCのAIクライアントで試すための構成です。
ChatGPTのクラウド接続に必要な公開HTTPS・OAuth認証やトンネル設定は含めていません。
将来その接続を追加するときも、共有の読み取り処理を再利用できます。

## WebMCPで接続する

WebMCP対応のChatGPT/CodexブラウザーでCRECのページを開き、
「このプロジェクトのカメラを検索して」のように依頼します。
ページが共通カタログを取得し、`document.modelContext.registerTool` を通して9つの操作を登録します。
別途MCP接続を登録する必要はありません。
対応していないブラウザーでは、通常のCREC画面として動作します。

WebMCPの読み取りは、そのページのプロジェクト世代に固定されます。
検索結果をAIへ返し、表示中の検索フォームや未保存の入力は変更しません。
プロジェクトの切替を検出すると登録を解除し、実行中の読み取りも中止します。
ページが古くなった場合は、新しいプロジェクトの画面を開いてから再度依頼してください。

仕様・接続方法の参考:

- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html)
- [WebMCP仕様草案](https://webmachinelearning.github.io/webmcp/)
- [ChatGPTのWebMCP](https://learn.chatgpt.com/docs/webmcp)
- [OpenAIのプライベートMCP接続](https://developers.openai.com/blog/connect-private-mcp-servers-to-openai-products)
