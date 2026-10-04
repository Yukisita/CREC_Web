# 外部AIアプリからの読み取り

CRECは、外部AIアプリ用のMCPと、開いているページ用のWebMCPを提供します。
最初の実装は、現在のプロジェクト確認・コレクション検索・詳細取得の3操作です。
LLMの設定やAPIキーは不要です。保存・削除・在庫更新は公開していません。

```text
AIアプリ → MCP /mcp ────────────────────────┐
AIブラウザー → WebMCP → Web API ────────────┼→ CollectionQueryService → CrecDataService
```

読み取り処理は `CollectionQueryService` にまとめ、通信部分だけを分けます。
MCPは公式C# SDKのStreamable HTTPを使い、状態を接続ごとに保持しません。
アプリ内の[AIチャット](ai-chat.md)は、これまでどおりLLMへ直接接続します。

## 公開する操作

| 操作 | 内容 | MCPの引数 | WebMCPの引数 |
|---|---|---|---|
| `get_current_project` | プロジェクト名・世代・選択状態 | なし | なし |
| `search_collections` | 保存済みコレクションの検索 | `projectRevision`, `query`, `page`, `pageSize` | `query`, `page`, `pageSize` |
| `get_collection` | IDによる詳細取得 | `projectRevision`, `collectionId` | `collectionId` |

検索は既存の検索処理を使い、名称・ID・管理コード・カテゴリ・タグ・場所を部分一致で探します。
`query` は省略すると一覧取得となり、最大256文字です。
`page` は1〜1,000,000（既定1）、`pageSize` は1〜50（既定20）です。
`collectionId` は検索結果の `id` をそのまま使います。

結果には保存済みのメタデータと在庫状況を含めます。
`currentInventory` は整数の精度を保つため文字列、未設定なら `null` です。
ファイルパスや添付ファイルの内容は返しません。返された文字列はデータとして扱います。

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
ページが `document.modelContext.registerTool` を通して3つの操作を登録します。
別途MCP接続を登録する必要はありません。
対応していないブラウザーでは、通常のCREC画面として動作します。

WebMCPの読み取りは、そのページのプロジェクト世代に固定されます。
検索結果をAIへ返し、表示中の検索フォームや未保存の入力は変更しません。
プロジェクトの切替を検出すると登録を解除し、実行中の読み取りも中止します。
ページが古くなった場合は、新しいプロジェクトの画面を開いてから再度依頼してください。

## 開発と確認

検索用Web APIは `GET /api/collection-queries`、詳細は
`GET /api/collection-queries/{collectionId}` です。どちらも `projectRevision` が必須です。
読み取り中は既存のプロジェクト受付ハンドルを保持し、切替によるデータの混在を防ぎます。
MCPの探索は未選択状態でも利用でき、コレクション取得は選択後に限ります。
古い世代は `projects-stale`、切替中は `projects-busy` で拒否します。

[テスト手順](../tests/README.md)には、公式MCPクライアントでの通信と、
WebMCPの登録・中止・ページ復帰を確認するテストを記載しています。
Codexのブラウザーでも、検証用プロジェクトで3操作の検出と実行を確認しています。

仕様・接続方法の参考:

- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html)
- [WebMCP仕様草案](https://webmachinelearning.github.io/webmcp/)
- [ChatGPTのWebMCP](https://learn.chatgpt.com/docs/webmcp)
- [OpenAIのプライベートMCP接続](https://developers.openai.com/blog/connect-private-mcp-servers-to-openai-products)
