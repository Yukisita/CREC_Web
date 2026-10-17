# データファイル追加の回帰テスト

```powershell
dotnet run --project tests/DataUpload.Tests -p:BaseOutputPath=bin/hotfix-data-upload/
node --test tests/DataUpload.Tests/data-file-manager.test.cjs
```

4.8GB・8GBちょうど・8GB超過の判定、実際のHTTPアップロード、サイズ超過時の413、既存の400/409、画面の事前チェックと各言語の表示を確認します。上限のGBは既存の表示と同じ1024進です。大容量の境界値にはコピープローブを使用し、数GBの実ファイルは作成しません。実際の受信中の上限超過は、テスト専用フィルターで上限を下げて確認します。
