# CREC Web AI 操作リファレンス

チャットは `ChatService` がLLMへ直接問い合わせ、`ChatActionPolicy` が検証した操作計画をブラウザで実行します。
接続設定と設計は [AIチャット](../../docs/ai-chat.md)、プロンプトは `ChatSystem.txt` を参照してください。

## 応答形式

モデルは `text` と `actions` を持つJSONを返します。

```json
{
  "text": "カメラを検索します。",
  "actions": [{ "type": "search", "text": "カメラ" }]
}
```

Web APIはこれにサーバーが設定する `warning` を付けます。表示文は操作として解釈しません。
以下のワークフローのJSON配列は `actions` の内容です。

## アクション一覧

| type | 引数 | 動作 |
| --- | --- | --- |
| `search` | `text` | ホームページで検索。キーワードは翻訳しない |
| `openCollectionByName` | `name` | 表示中の名前に一致するコレクションの概要を開く |
| `navigateToCollectionByName` | `name` | 表示中のコレクションの詳細へ移動 |
| `showCollectionPanel` | `id` | IDで概要を開く。ホーム以外では詳細へ移動 |
| `showAdminPanel` | なし | 管理パネルを開く |
| `createNewCollection` | なし | 作成後に詳細へ移動し、編集モーダルを開く |
| `navigateHome` | なし | ホームへ移動 |
| `navigate` | `path` | 同一オリジンのパスへ移動。外部URLは禁止 |
| `clickButton` | `id` | 許可されたボタンを実行 |
| `fillInput` | `id`, `value` | 許可された入力欄へ文字列または数値を設定 |
| `switchLanguage` | `lang` | `ja` / `en` / `de` に表示言語を変更 |

名前の照合は完全一致、大文字・小文字を無視した完全一致、部分一致の順です。
名前は表示中の検索結果で照合します。目的のコレクションがなければ先に検索してください。
操作には `type` と上記の引数だけを含め、任意の追加プロパティは許可しません。

## 許可されたボタン

| ID | 動作 |
| --- | --- |
| `searchButton`, `clearFiltersButton` | 検索、フィルター解除 |
| `toggleAdvancedFiltersButton` | 詳細フィルターの開閉 |
| `gridViewBtn`, `tableViewBtn` | 表示形式の変更 |
| `adminPanelToggle` | 管理パネルの開閉 |
| `addNewCollectionBtn` | 新規作成（`createNewCollection` を優先） |
| `editProjectBtn` | 設定画面（同じ画面で続けるには `navigate` を使用） |
| `editIndexBtn`, `saveIndexEdit` | 詳細ページのコレクション編集、保存 |
| `inventoryOperationBtn`, `inventoryOperationSave`, `inventoryOperationCancel` | 在庫操作の開始、保存、取消 |
| `inventoryManagementSettingsBtn`, `inventoryManagementSettingsSave`, `inventoryManagementSettingsCancel` | 在庫設定の開始、保存、取消 |
| `projectEditSaveBtn` | プロジェクト設定を保存 |
| `openProjectBtn` | プロジェクト選択画面を開く。Desktopではファイル選択 |
| `refreshProjectsBtn`, `cancelProjectSelectionBtn` | Webのプロジェクト候補再取得、選択解除 |

`deleteCollectionBtn` は禁止します。`confirmProjectSwitchBtn` は許可しません。
プロジェクトの選択と確定は画面で行います。
非表示、無効、閉じたパネル・モーダル内のボタンは実行できません。

## 許可された入力欄

| ID | 値 |
| --- | --- |
| `searchText` | 検索語 |
| `searchField`, `searchMethod`, `inventoryStatusFilter` | 現在の選択肢の値 |
| `operationType` | `0`=入庫、`1`=出庫、`2`=棚卸し |
| `operationQuantity` | 入庫は正、出庫は負、棚卸しは絶対数量 |
| `operationComment` | 在庫操作のコメント |
| `safetyStock`, `reorderPoint`, `maximumLevel` | 安全在庫、発注点、最大在庫 |
| `editName`, `editManagementCode`, `editRegistrationDate`, `editCategory` | 名前、管理コード、登録日、カテゴリ |
| `editFirstTag`, `editSecondTag`, `editThirdTag`, `editLocation` | タグ、場所 |
| `editProjectName` | プロジェクト名 |
| `editCollectionNameLabel`, `editUUIDLabel`, `editManagementCodeLabel`, `editCategoryLabel` | 項目の表示名 |
| `editTag1Label`, `editTag2Label`, `editTag3Label` | タグ項目の表示名 |

値を入力後、ブラウザの入力検証を通過することを確認します。
読み取り専用欄への書き込み、不正な選択肢、非有限数は拒否します。
入力イベントを発行するため、既存の画面と未保存入力の管理に反映されます。

## ワークフロー例

### 別画面から検索

```json
[{"type":"navigateHome"},{"type":"search","text":"カメラ"}]
```

### コレクション名を変更

詳細ページで編集モーダルが閉じている場合:

```json
[
  {"type":"clickButton","id":"editIndexBtn"},
  {"type":"fillInput","id":"editName","value":"新しいカメラ"},
  {"type":"clickButton","id":"saveIndexEdit"}
]
```

モーダルが既に開いている場合は、開始操作を省略します。
新規作成なら `createNewCollection` に続けて入力と保存を指定できます。

### 3個を出庫

```json
[
  {"type":"clickButton","id":"inventoryOperationBtn"},
  {"type":"fillInput","id":"operationType","value":"1"},
  {"type":"fillInput","id":"operationQuantity","value":"-3"},
  {"type":"clickButton","id":"inventoryOperationSave"}
]
```

### プロジェクト選択画面を開く

```json
[{"type":"showAdminPanel"},{"type":"clickButton","id":"openProjectBtn"}]
```

## 実行と継続

計画は32操作までで、開始前に全体を検証します。一つでも不正なら全体を拒否します。
実行時は各操作前に400msのUI遷移待ちを置き、非同期処理の完了も待ちます。
失敗したら後続操作を停止します。実行済みの変更を自動で巻き戻すことはありません。

ページ遷移後は初期化を待って再開します。保留操作はプロジェクト世代・遷移先を照合し、5分で失効します。
同じページへの移動ではリロードを挟まず続行します。
履歴は20件を保持し、操作結果を追記します。プロジェクト切替やサーバー再起動では破棄します。
未選択・古い画面での送信を無効にし、API側でも共通ミドルウェアで世代を検証します。

## 操作の追加

`ChatActionPolicy` の操作定義と許可一覧、ブラウザの `CHAT_ACTION_FIELDS` と実行処理を更新します。
C#の定義からLLMへ渡すJSON Schemaも更新されます。
`tests/chat-action-contract.json` の共通例、`ChatSystem.txt`、このリファレンスも更新してください。

非同期処理はボタンの `chatAction` に既存ハンドラーを公開し、成功時 `true`、失敗時 `false` を返します。
テスト手順は [tests/README.md](../../tests/README.md) を参照してください。
