# CREC Web AI 操作リファレンス

AIチャットウィジェットが実行できる操作の完全リファレンスです。  
サーバ側のシステムプロンプト（`Prompts/system_prompt.ja.txt` / `system_prompt.en.txt` / `system_prompt.de.txt`）には最小限の内容のみ記載されており、詳細はこのドキュメントで管理します。

---

## アクション一覧

### `search` — キーワード検索

ホームページでコレクションを検索します。  
**注意:** ホームページ上でのみ有効です。他のページにいる場合は先に `navigate` で移動してください。

```json
{"type": "search", "text": "検索キーワード"}
```

---

### `openCollection` — コレクションを開く

指定 ID のコレクション詳細を新しいタブで開きます。

```json
{"type": "openCollection", "id": "コレクションID"}
```

---

### `showAdminPanel` — 管理パネルを表示

管理パネル（コレクション追加・削除・設定）を表示します。

```json
{"type": "showAdminPanel"}
```

---

### `navigate` — ページ移動

同一サーバー内の任意のパスへ移動します。  
すでに同じページにいる場合はリロードしません。

```json
{"type": "navigate", "path": "/パス"}
```

| パス | 移動先 |
|------|--------|
| `/` | ホーム（コレクション一覧） |
| `/ProjectEdit` | プロジェクト設定 |

---

### `clickButton` — ボタンをクリック

指定 ID のボタンをクリックします。使用可能な ID は以下の通りです。

```json
{"type": "clickButton", "id": "ボタンID"}
```

| ID | 説明 | 有効ページ |
|----|------|-----------|
| `addNewCollectionBtn` | 新規コレクション作成 | 管理パネルが開いているとき |
| `editProjectBtn` | プロジェクト設定を開く | 全ページ |
| `adminPanelToggle` | 管理パネルを開閉 | 全ページ |
| `searchButton` | 検索実行 | ホームページ |
| `clearFiltersButton` | フィルタクリア | ホームページ |
| `inventoryOperationBtn` | 在庫操作モーダルを開く | コレクション詳細パネルが開いているとき |
| `inventoryManagementSettingsBtn` | 在庫管理設定モーダルを開く | コレクション詳細パネルが開いているとき |
| `inventoryOperationSave` | 在庫操作を保存 | 在庫操作モーダルが開いているとき |
| `inventoryOperationCancel` | 在庫操作をキャンセル | 在庫操作モーダルが開いているとき |
| `inventoryManagementSettingsSave` | 在庫管理設定を保存 | 在庫管理設定モーダルが開いているとき |
| `inventoryManagementSettingsCancel` | 在庫管理設定をキャンセル | 在庫管理設定モーダルが開いているとき |
| `editIndexBtn` | インデックス編集モーダルを開く | コレクション詳細ページのみ |

---

### `fillInput` — フィールドに入力

指定 ID のフォームフィールドに値を入力します。

```json
{"type": "fillInput", "id": "フィールドID", "value": "入力値"}
```

| ID | 説明 | 入力値 |
|----|------|--------|
| `searchText` | 検索キーワード | テキスト |
| `operationType` | 在庫操作タイプ | `0` = 入庫 / `1` = 出庫 / `2` = 棚卸し |
| `operationQuantity` | 在庫操作数量 | 数値（例: `5`） |
| `operationComment` | 在庫操作コメント | テキスト |
| `safetyStock` | 安全在庫数 | 数値 |
| `reorderPoint` | 発注点 | 数値 |
| `maximumLevel` | 最大在庫数 | 数値 |

---

## ワークフロー例

複数のアクションは **上から順に 600ms 間隔** で自動実行されます。  
モーダルが開くのを待ってから入力・保存するため、必ず以下の順序を守ってください。

---

### 在庫入庫（例: 5個入庫、コメント「補充」）

**ユーザー発言例:** 「在庫を5個追加して、コメントは補充で」

```
<action>{"type":"clickButton","id":"inventoryOperationBtn"}</action>
<action>{"type":"fillInput","id":"operationType","value":"0"}</action>
<action>{"type":"fillInput","id":"operationQuantity","value":"5"}</action>
<action>{"type":"fillInput","id":"operationComment","value":"補充"}</action>
<action>{"type":"clickButton","id":"inventoryOperationSave"}</action>
```

**手順の説明:**
1. `inventoryOperationBtn` → 在庫操作モーダルを開く
2. `operationType = 0` → 入庫を選択
3. `operationQuantity = 5` → 数量を入力
4. `operationComment = 補充` → コメントを入力
5. `inventoryOperationSave` → 保存

---

### 在庫出庫（例: 3個出庫）

**ユーザー発言例:** 「在庫を3個出庫して」

```
<action>{"type":"clickButton","id":"inventoryOperationBtn"}</action>
<action>{"type":"fillInput","id":"operationType","value":"1"}</action>
<action>{"type":"fillInput","id":"operationQuantity","value":"3"}</action>
<action>{"type":"clickButton","id":"inventoryOperationSave"}</action>
```

---

### 新規コレクション作成

**ユーザー発言例:** 「新しいコレクションを作成して」

```
<action>{"type":"showAdminPanel"}</action>
<action>{"type":"clickButton","id":"addNewCollectionBtn"}</action>
```

> `addNewCollectionBtn` をクリックすると、新しいタブにコレクション詳細ページが開き、インデックス編集モーダルが自動的に表示されます。

---

### キーワード検索

**ユーザー発言例:** 「カメラで検索して」

ホームページにいる場合:
```
<action>{"type":"search","text":"カメラ"}</action>
```

他のページにいる場合（ホームページへ移動してから検索）:
```
<action>{"type":"navigate","path":"/"}</action>
<action>{"type":"search","text":"カメラ"}</action>
```

---

### 在庫管理設定の変更（例: 安全在庫を10に変更）

**ユーザー発言例:** 「安全在庫を10に設定して」

```
<action>{"type":"clickButton","id":"inventoryManagementSettingsBtn"}</action>
<action>{"type":"fillInput","id":"safetyStock","value":"10"}</action>
<action>{"type":"clickButton","id":"inventoryManagementSettingsSave"}</action>
```

---

## カスタマイズ方法

### ボタン・フィールドの追加

新しい操作を AI に許可するには、以下の箇所を変更してください:

1. **`CREC_MCPServer/.env`** — `SAFE_BUTTON_IDS` または `SAFE_INPUT_IDS` に新しい ID を追加
2. **このドキュメント** — 上記のテーブルとワークフロー例を更新

MCP サーバは環境変数に記載のない ID を自動的に除去するため、ホワイトリストに追加しない限り AI はその要素を操作できません。

### システムプロンプトの編集

`CREC_MCPServer/prompts/system_prompt.{lang}.txt` を直接編集することで、再コンパイルなしにプロンプトを変更できます。  
ファイルはサーバ起動時にキャッシュされるため、変更後はサーバを再起動してください。

---

## 技術仕様

| 項目 | 内容 |
|------|------|
| アクション実行間隔 | 600ms（`CHAT_ACTION_INTERVAL` 定数） |
| 初回アクション待機 | 400ms（`CHAT_ACTION_INITIAL_DELAY` 定数） |
| 会話履歴保持 | `sessionStorage`（ページ遷移後も維持） |
| 最大履歴件数 | 20件（`CHAT_HISTORY_MAX` 定数） |
| ホワイトリスト検証 | MCP サーバ側（`_sanitize_response()` 関数） |
| プロンプトキャッシュ | MCP サーバプロセス内メモリ（サーバ再起動でリセット） |
| MCP トランスポート | Streamable HTTP (`POST /mcp`) |
| MCP セッション | サーバプロセス起動後に 1 回初期化、以降は再利用 |

---

*このドキュメントは `Prompts/CREC_Web_AI_Operations.md` として管理されており、GitHub Wiki にそのまま掲載できます。*
