# FlowRecord
パソコンの起動やシャットダウン、アクティブウィンドウを記録するアプリです。

## to do list
- [x] タスクトレイアイコンに設定する方法
- [x] 正式リリースの方法
- [ ] アイコンの作成
- [x] データベースをsupabaseに変更
- [x] アプリを起動したときはウィンドウを表示せず、バックグラウンドで実行する。
  - [x] タスクトレイアイコンの"Open"をおしたときにだけ、起動する
- [x] データベースをpc_name, active_window, boot_shutdownに分ける
- [x] アプリを閉じてもboot_shutdownテーブルのshutdown_timeに記録されない
- [x] <span style="color: red; ">スタートアップアプリが起動するときもアクティブウインドウに記録されてしまう</span>
- [x] アクティブウインドウに"システム トレイ オーバーフロー ウィンドウ"が記録されてしまう。
- [x] パソコンを起動したときとシャットダウンしたときだけboot_shutdownテーブルに記録するようにする
- [x] シャットダウンしたときにテーブルに記録されない
- [x] アプリを起動してもコンテンツメニューにアイコンが表示されない。
- [x] "pending_shutdown.json"が生成されない
- [x] shutdown.logファイルにシャットダウンした時間だけを記録する
- [x] パソコンを起動したときにshutdown.logファイルに保存した時間をboot_shutdownテーブルのpc_nameが一致する最後のレコードに保存する
- [x] shutdown.txtファイルの一番最後の行だけ読み込む

## 課題
- "Exit"ボタンを押したらパソコンのシャットダウン時間を記録できなくなる
  - "Exit"ボタンを押すときに警告を出す

## ビルド方法

### バックエンドのビルド
```bash
cd FlowRecord/
dotnet publish -c Release -r win-x64 --self-contained true
```

### フロントエンドのビルド
```bash
cd frontend/
npm run build
```
