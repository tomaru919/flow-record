# FlowRecord
パソコンの起動やシャットダウン、アクティブウィンドウを記録するアプリです。

## Database
- ローカルSQLite（`%LocalAppData%\FlowRecord\flowrecord.db`、DEBUGビルドは `flowrecord.debug.db`）。スキーマは起動時に `MonitorService.InitializeDatabase` が作成する。
- 日時は `TEXT` で保存し、比較・計算は `julianday()` などSQLiteの関数で行う。
- 終了時刻が `NULL` の行を `@now` まで延長してよいのは、現在進行中の行（メモリ上の `_bootShutdownId` / `_currentWindowRecordId` / `_sleepWakeId`）だけ。それ以外は強制終了などで残った孤立行なので、開始時刻にフォールバックさせ0時間として扱う。

## Session Log
セッションごとの変更は `SESSION_LOG.md` に記録する（日付・タイトル・変更内容の要約）。

## document
ユーザーの技術的な質問への回答は、Notion の MCP で `FlowRecord ナレッジ` データベース（データソース: `collection://b9890974-7f71-4dfc-b17f-eb3488e88107`）にページとして保存してください。リポジトリの `docs/` には保存しません。
- プロパティ: `名前`（内容を表す日本語のタイトル）、`カテゴリ`（`C#・.NET` / `WPF・WebView2` / `Windows API` / `SQLite` / `React・フロントエンド` / `Git` / `ビルド・リリース` / `設計・改善` から 1 つ）。本文は Notion Markdown。
- 以前の回答の続きなら、`親項目` にそのページを設定する（小さな追記なら元のページの末尾に足す）。
- Google Drive の `FlowRecord` フォルダーは移行前の旧保存先で、新しいファイルは追加しません。
