# AIチャットの構成と設定

CREC Webのチャットは、ASP.NET CoreからLLMのAPIを直接呼びます。

```text
ブラウザ → POST /api/Chat → ChatService → POST {AiChat:BaseUrl}/chat/completions
ブラウザ ← 表示文と検証済みの操作計画 ← ChatActionPolicy ← LLMのJSON応答
```

## この構成を選ぶ理由

この機能は、表示中の画面を説明し、その画面で利用できる操作を行うものです。
アプリ内チャットでは、Webサーバーに処理をまとめてLLMへ直接接続します。
外部AIアプリからの読み取りには、共有の業務サービスを呼ぶMCPとWebMCPを提供します。
接続方法と公開操作は[外部AI連携](ai-tools.md)を参照してください。
アプリ内チャットの通信をMCPへ変更する必要はありません。

画面操作はブラウザで行い、既存の入力検証、保存処理とプロジェクト世代の確認を共有します。
LLMへの問い合わせは一回で操作計画を生成し、その実行結果を次の会話へ渡します。
サーバーで画面操作を再実装したり、モデルが自動で何度も問い合わせるループは設けません。

## 設定

`CREC_Web/appsettings.json` の `AiChat` を設定します。Web版とDesktop版で共通です。
Pythonのインストールやチャット中継サーバーの起動は不要です。

```json
{
  "AiChat": {
    "BaseUrl": "http://127.0.0.1:1234/v1/",
    "Model": "使用するモデルの識別子",
    "TimeoutSeconds": 120,
    "MaxContextCharacters": 3000,
    "MaxHistoryTurns": 10
  }
}
```

`BaseUrl` はAPIの基点を指定します。LM Studioは通常 `http://127.0.0.1:1234/v1/`、
Ollamaは通常 `http://127.0.0.1:11434/v1/` です。別のパスを使うプロキシも指定できます。
`chat/completions` はアプリが末尾に追加します。モデル識別子はバックエンドに読み込んだものを指定してください。

APIキーが必要なら `AiChat__ApiKey` 環境変数またはASP.NET Coreのシークレット設定を使います。
`AiChat:ApiKey` はWebサーバーから送るBearer認証に使用します。
`AiChat__BaseUrl` などの環境変数でも設定を上書きできます。

接続先には `/v1/chat/completions` と `response_format.type=json_schema` に対応したAPIとモデルが必要です。
[LM Studioの構造化出力](https://lmstudio.ai/docs/developer/openai-compat/structured-output)と
[Ollamaの互換API](https://docs.ollama.com/api/openai-compatibility)に合わせています。
非対応のバックエンドにはエラーを返します。

## 応答と操作の境界

モデルは `{ "text": "説明", "actions": [...] }` というJSONを返します。
操作の種類・引数・ボタンや入力欄の許可一覧は `ChatActionPolicy` で定義し、
同じ定義からモデルへ送るJSON Schemaを生成します。
Schemaによる出力制限に加えて、受信した計画をC#とブラウザで検証します。

不正な操作が一つでも含まれた計画は全体を拒否します。コレクション削除は許可しません。
途中で切れた応答、コードブロック、壊れたJSONも実行しません。
操作は32件までで、保存などの非同期処理を順に待ち、失敗したら後続を止めます。
先に成功した操作を自動で巻き戻す仕組みはありません。

APIの `warning` はサーバーが設定する `invalid_actions` または `deletion_blocked` です。
表示文に含まれるタグやコードは実行されません。
通信失敗やタイムアウトは自動再送せず、応答本文の読み込みもタイムアウト・中止の対象に含めます。

会話と保留操作はプロジェクト世代に結び付け、通常のページ遷移で維持します。
切替を検出すると応答待ちを中止し、履歴と保留操作を破棄します。
プロジェクト未選択時の送信は無効です。API側も共通ミドルウェアで世代を確認します。

## 開発と検証

システムプロンプトは `CREC_Web/Prompts/ChatSystem.txt` にあり、ビルド・発行時にコピーされます。
変更後はWebサーバーを再起動してください。ログには処理時間、操作件数と警告を記録します。

操作一覧は [操作リファレンス](../CREC_Web/Prompts/CREC_Web_AI_Operations.md)、
検証手順は [テスト手順](../tests/README.md) を参照してください。
自動テストは模擬LLMで通信と操作検証を確認します。
実モデルによる指示理解や操作計画の精度は、使用するモデルで別途確認する必要があります。
