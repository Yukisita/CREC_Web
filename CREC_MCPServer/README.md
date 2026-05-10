# CREC MCPServer

CREC Web 用 Python MCP サーバです。  
ローカル LLM（Ollama・LM Studio 等、OpenAI 互換 API）と連携し、CREC Web の AI チャット機能を提供します。  
CREC Web の C# バックエンドが MCP クライアントとして接続し、`process_chat` ツールを呼び出します。

---

## アーキテクチャ

```
ブラウザ（チャット UI）
    ↓ POST /api/Chat
CREC Web C# バックエンド（MCP クライアント）
    ↓ MCP tools/call  HTTP POST /mcp
CREC MCPServer（本サーバ、Python）
    ↓ POST /v1/chat/completions
LLM バックエンド（Ollama / LM Studio 等）
```

---

## 必要条件

- Python 3.11 以上
- pip
- OpenAI 互換 LLM バックエンド（Ollama / LM Studio 等）が別途起動済みであること

---

## セットアップ

```bash
# 1. リポジトリルートから CREC_MCPServer ディレクトリへ移動
cd CREC_MCPServer

# 2. 仮想環境の作成（推奨）
python -m venv .venv
source .venv/bin/activate       # Windows: .venv\Scripts\activate

# 3. 依存パッケージのインストール
pip install -r requirements.txt

# 4. 環境変数の設定
cp .env.example .env
# .env をテキストエディタで開いて LLM_URL / LLM_MODEL 等を設定する
```

---

## 起動

```bash
# 仮想環境を有効化した状態で
python server.py
```

起動すると以下のようなメッセージが表示されます：

```
Starting CREC Web MCP Server on 127.0.0.1:8765
LLM backend: http://localhost:11434  model: llama3.2
```

---

## 設定項目（環境変数）

| 変数名 | デフォルト値 | 説明 |
|--------|-------------|------|
| `LLM_URL` | `http://localhost:11434` | LLM バックエンドのベース URL（末尾スラッシュなし）。Ollama のデフォルト。LM Studio の場合は `http://localhost:1234`。 |
| `LLM_MODEL` | `llama3.2` | 使用するモデル名。Ollama の場合は `ollama pull` 済みのモデル名。 |
| `MCP_HOST` | `127.0.0.1` | MCP サーバのバインドアドレス。外部から接続する場合は `0.0.0.0` に変更。 |
| `MCP_PORT` | `8765` | MCP サーバのポート番号。CREC Web の `appsettings.json` の `McpServer:Url` と合わせること。 |
| `LLM_TIMEOUT` | `120` | LLM リクエストのタイムアウト（秒）。 |
| `SAFE_BUTTON_IDS` | （下記参照） | AI がクリックを許可されるボタン ID のカンマ区切りリスト。 |
| `SAFE_INPUT_IDS` | （下記参照） | AI が入力を許可されるフォームフィールド ID のカンマ区切りリスト。 |

環境変数は `.env` ファイルにまとめて記述するか、OS のシェル設定で指定してください。

---

## デフォルトのホワイトリスト

### ボタン ID（`SAFE_BUTTON_IDS`）

| ID | 説明 |
|----|------|
| `addNewCollectionBtn` | 新規コレクション作成 |
| `editProjectBtn` | プロジェクト設定を開く |
| `adminPanelToggle` | 管理パネルを開閉 |
| `searchButton` | 検索実行 |
| `clearFiltersButton` | フィルタクリア |
| `inventoryOperationBtn` | 在庫操作モーダルを開く |
| `inventoryManagementSettingsBtn` | 在庫管理設定モーダルを開く |
| `inventoryOperationSave` | 在庫操作を保存 |
| `inventoryOperationCancel` | 在庫操作をキャンセル |
| `inventoryManagementSettingsSave` | 在庫管理設定を保存 |
| `inventoryManagementSettingsCancel` | 在庫管理設定をキャンセル |
| `editIndexBtn` | インデックス編集モーダルを開く |

### フォームフィールド ID（`SAFE_INPUT_IDS`）

| ID | 説明 | 入力値 |
|----|------|--------|
| `operationType` | 在庫操作タイプ | `0`=入庫 / `1`=出庫 / `2`=棚卸し |
| `operationQuantity` | 在庫操作数量 | 数値 |
| `operationComment` | 在庫操作コメント | テキスト |
| `searchText` | 検索キーワード | テキスト |
| `safetyStock` | 安全在庫数 | 数値 |
| `reorderPoint` | 発注点 | 数値 |
| `maximumLevel` | 最大在庫数 | 数値 |
| `searchField` | 検索対象フィールド | テキスト |
| `searchMethod` | 検索方式 | テキスト |
| `inventoryStatusFilter` | 在庫状況フィルタ | テキスト |

---

## 公開ツール一覧

| ツール名 | 説明 |
|---------|------|
| `process_chat(message, history, page_context, page_title, lang, project_name)` | メインツール。LLM を呼び出してチャット応答を生成し、アクション ID をホワイトリスト検証して返す。 |
| `search_collections(keyword)` | コレクション検索アクションタグを返す。 |
| `navigate(path)` | ページ遷移アクションタグを返す。 |
| `show_admin_panel()` | 管理パネル表示アクションタグを返す。 |
| `click_button(button_id)` | ホワイトリスト検証済みボタンクリックアクションタグを返す。 |
| `fill_input(field_id, value)` | ホワイトリスト検証済みフォーム入力アクションタグを返す。 |

---

## システムプロンプトのカスタマイズ

`prompts/system_prompt.{lang}.txt`（`ja` / `en` / `de`）を直接編集することで、再起動なしにプロンプトを変更できます。  
ファイルは起動時にキャッシュされるため、変更後はサーバを再起動してください。

プレースホルダー:

| プレースホルダー | 置換内容 |
|----------------|---------|
| `{{projectName}}` | CREC Web プロジェクト名 |
| `{{pageTitle}}` | 現在のページタイトル |
| `{{context}}` | 現在のページ内容（抜粋） |

---

## CREC Web との接続設定

CREC Web の `appsettings.json` で以下を設定してください：

```json
{
  "McpServer": {
    "Url": "http://127.0.0.1:8765"
  },
  "LlmBackend": {
    "Url": "http://localhost:11434",
    "Model": "llama3.2"
  }
}
```

> **注意**: `McpServer:Url` は本サーバの `MCP_HOST:MCP_PORT` と一致させてください。  
> `LlmBackend` の設定は CREC Web 側には不要ですが、参考のために残しています（LLM の設定は本サーバ側の環境変数で行います）。

---

## トラブルシューティング

### LLM バックエンドに接続できない

- `LLM_URL` が正しいか確認（末尾スラッシュなし）
- Ollama の場合: `ollama serve` が起動していること、モデルが `ollama pull <model>` 済みであること
- LM Studio の場合: "Start Server" が実行済みであること

### CREC Web から接続できない

- `MCP_PORT` と CREC Web の `McpServer:Url` のポートが一致しているか確認
- ファイアウォールでポートが許可されているか確認
- 外部ホストから接続する場合は `MCP_HOST=0.0.0.0` に設定

### アクションが実行されない

- ボタン/フィールド ID が `SAFE_BUTTON_IDS` / `SAFE_INPUT_IDS` に含まれているか確認
- サーバログで `[ERROR]` メッセージを確認
