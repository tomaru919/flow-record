# FlowRecord
パソコンの起動やシャットダウン、アクティブウィンドウを記録するアプリです。

## Database
- ローカルSQLite（`%LocalAppData%\FlowRecord\flowrecord.db`、DEBUGビルドは `flowrecord.debug.db`）。スキーマは起動時に `MonitorService.InitializeDatabase` が作成する。
- 日時は `TEXT` で保存し、比較・計算は `julianday()` などSQLiteの関数で行う。
- 終了時刻が `NULL` の行を `@now` まで延長してよいのは、現在進行中の行（メモリ上の `_bootShutdownId` / `_currentWindowRecordId` / `_sleepWakeId`）だけ。それ以外は強制終了などで残った孤立行なので、開始時刻にフォールバックさせ0時間として扱う。

## Session Log
セッションごとの変更は `SESSION_LOG.md` に記録する（日付・タイトル・変更内容の要約）。
