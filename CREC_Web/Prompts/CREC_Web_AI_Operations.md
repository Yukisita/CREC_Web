# CREC Web AI 操作リファレンス

AIチャットウィジェットが実行できる操作の完全リファレンスです。  
サーバ側のシステムプロンプトは `CREC_MCPServer/prompts/system_prompt.txt` で管理されています。詳細な操作リファレンスはこのドキュメントを参照してください。

---

## アクション一覧

### `search` — キーワード検索

ホームページでコレクションを検索します。  
**注意:** ホームページ上でのみ有効です。他のページにいる場合は先に `navigate` で移動してください。

```json
{"type": "search", "text": "検索キーワード"}
```

---

### `openCollectionByName` — 名前でコレクション概要を開く

表示中のコレクションから名前を照合し、ホームページでは概要サイドパネルを開きます。ホームページ以外では同一ウィンドウの詳細ページへ移動します。

```json
{"type": "openCollectionByName", "name": "コレクション名"}
```

名前の照合は、完全一致、大文字・小文字を無視した完全一致、部分一致の順に行われます。

---

### `navigateToCollectionByName` — 名前でコレクション詳細へ移動

表示中のコレクションから名前を照合し、同一ウィンドウの詳細ページへ移動します。

```json
{"type": "navigateToCollectionByName", "name": "コレクション名"}
```

---

### `showCollectionPanel` — コレクション詳細パネルを開く

指定 ID のコレクション詳細をホームページのサイドパネルで開きます。  
ホームページ以外では同一ウィンドウの詳細ページへ移動します。

```json
{"type": "showCollectionPanel", "id": "コレクションID"}
```

> **使い分けの目安:** ID が分かっている場合は `showCollectionPanel`、名前が分かっている場合は `openCollectionByName` を使用します。明示的に詳細ページへ移動する場合は `navigateToCollectionByName` を使用してください。

---

### `showAdminPanel` — 管理パネルを表示

管理パネル（コレクション追加・削除・設定）を表示します。

```json
{"type": "showAdminPanel"}
```

---

### `createNewCollection` — 新規コレクションを作成

新しいコレクションをサーバ側で作成し、そのまま **同一ウィンドウ内** でコレクション詳細ページに遷移してインデックス編集モーダルを自動表示します。

```json
{"type": "createNewCollection"}
```

> **注意:** 引数は不要です。作成後のページ遷移前に追加のアクションが必要な場合（例: フィールドへの入力）は、`createNewCollection` の後ろにそのアクションを続けてください。ページ遷移後に自動的に実行されます。

---

### `navigate` — ページ移動

同一サーバー内の任意のパスへ移動します。  
すでに同じページにいる場合はリロードせず、後続アクションをその場で実行します。

```json
{"type": "navigate", "path": "/パス"}
```

| パス | 移動先 |
|------|--------|
| `/` | ホーム（コレクション一覧） |
| `/ProjectEdit` | プロジェクト設定 |

> **ページ遷移をまたぐアクション列:** `navigate` または `createNewCollection` の後ろに続くアクションは、ページ遷移後の新しいページで自動的に実行されます。例えば `navigate` でホームに移動してから `search` を実行する場合、2 つのアクションを順番に並べるだけで機能します（詳細はワークフロー例を参照）。

---

### `navigateHome` — ホームへ移動

同一ウィンドウでホーム（コレクション一覧）へ移動します。ホームへ戻る指示には、`navigate` よりこちらを優先します。

```json
{"type": "navigateHome"}
```

---

### `switchLanguage` — 表示言語を切り替える

表示言語を日本語、英語、ドイツ語のいずれかへ切り替えます。

```json
{"type": "switchLanguage", "lang": "ja"}
```

`lang` に指定できる値は `ja`、`en`、`de` のみです。

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
| `projectEditSaveBtn` | プロジェクト設定を保存 | プロジェクト設定ページ |
| `saveIndexEdit` | インデックス編集内容を保存 | インデックス編集モーダルが開いているとき |
| `toggleAdvancedFiltersButton` | 詳細フィルタを開閉 | ホームページ |
| `gridViewBtn` | グリッド表示へ切り替え | ホームページ |
| `tableViewBtn` | テーブル表示へ切り替え | ホームページ |

`deleteCollectionBtn` はハードコードされた禁止対象であり、環境変数のホワイトリストへ追加してもAIから実行できません。

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
| `operationQuantity` | 在庫操作数量 | 入庫は正の数（例: `5`）、出庫は負の数（例: `-3`）、棚卸しは絶対量（例: `100`） |
| `operationComment` | 在庫操作コメント | テキスト |
| `safetyStock` | 安全在庫数 | 数値 |
| `reorderPoint` | 発注点 | 数値 |
| `maximumLevel` | 最大在庫数 | 数値 |
| `searchField` | 検索対象フィールド | 選択肢の値 |
| `searchMethod` | 検索方法 | 選択肢の値 |
| `inventoryStatusFilter` | 在庫状態フィルタ | 選択肢の値 |
| `editName` | コレクション名 | テキスト |
| `editManagementCode` | 管理コード | テキスト |
| `editRegistrationDate` | 登録日 | 日付 |
| `editCategory` | カテゴリ | テキスト |
| `editFirstTag` | タグ1 | テキスト |
| `editSecondTag` | タグ2 | テキスト |
| `editThirdTag` | タグ3 | テキスト |
| `editLocation` | 場所 | テキスト |
| `editProjectName` | プロジェクト名 | テキスト |
| `editCollectionNameLabel` | コレクション名ラベル | テキスト |
| `editUUIDLabel` | UUIDラベル | テキスト |
| `editManagementCodeLabel` | 管理コードラベル | テキスト |
| `editCategoryLabel` | カテゴリラベル | テキスト |
| `editTag1Label` | タグ1ラベル | テキスト |
| `editTag2Label` | タグ2ラベル | テキスト |
| `editTag3Label` | タグ3ラベル | テキスト |

---

## ページコンテキスト — 表示中コレクション一覧

ホームページで検索結果が表示されている場合、`{{context}}` の先頭に以下の形式でコレクション一覧が自動挿入されます。

```
[visible collections (N)]
{"name":"コレクション名A","id":"ID-A"}
{"name":"コレクション名B","id":"ID-B"}
...
```

この情報を使って、「表示中の最初のコレクションを開いて」などの指示に対して、正確な ID を使った `showCollectionPanel`、または名前を使った `openCollectionByName` / `navigateToCollectionByName` アクションを実行してください。

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

### 在庫出庫（例: 3個出庫）— 出庫は必ず負の数量

**ユーザー発言例:** 「在庫を3個出庫して」

```
<action>{"type":"clickButton","id":"inventoryOperationBtn"}</action>
<action>{"type":"fillInput","id":"operationType","value":"1"}</action>
<action>{"type":"fillInput","id":"operationQuantity","value":"-3"}</action>
<action>{"type":"clickButton","id":"inventoryOperationSave"}</action>
```

> **注意:** 出庫（type=1）の数量は必ず**負の数**で指定してください（例: `-3`）。正の数を指定するとバリデーションエラーになります。

---

### 新規コレクション作成

**ユーザー発言例:** 「新しいコレクションを作成して」

```
<action>{"type":"createNewCollection"}</action>
```

> `createNewCollection` アクション 1 つで、コレクションを API 経由で作成し、コレクション詳細ページへ遷移してインデックス編集モーダルを自動表示します。

---

### 表示中のコレクション詳細を開く

**ユーザー発言例:** 「表示中の最初のコレクションの詳細を開いて」

コンテキストに含まれる `[visible collections]` リストから ID を取得して使用します。

```
<action>{"type":"showCollectionPanel","id":"<コンテキストから取得したID>"}</action>
```

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

1. **MCPサーバープロセスの環境変数** — `SAFE_BUTTON_IDS` または `SAFE_INPUT_IDS` に新しい ID を追加
2. **このドキュメント** — 上記のテーブルとワークフロー例を更新

`server.py` は `.env` ファイルを自動では読み込みません。シェル、サービス定義、または起動構成で環境変数を設定してからMCPサーバーを再起動してください。`SAFE_BUTTON_IDS` / `SAFE_INPUT_IDS` を指定すると既定リスト全体を上書きするため、引き続き許可する既存IDも含める必要があります。

MCP サーバは環境変数のホワイトリストにない ID を自動的に除去するため、ホワイトリストに追加しない限り AI はその要素を操作できません。

### システムプロンプトの編集

`CREC_MCPServer/prompts/system_prompt.txt` を直接編集することで、再コンパイルなしにプロンプトを変更できます。
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
