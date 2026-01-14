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
- [ ] <span style="color: red; ">スタートアップアプリが起動するときもアクティブウインドウに記録されてしまう</span>
- [ ] アクティブウインドウに"システム トレイ オーバーフロー ウィンドウ"が記録されてしまう。
- [ ] パソコンを起動したときとシャットダウンしたときだけboot_shutdownテーブルに記録するようにする
- [ ] シャットダウンしたときにテーブルに記録されない
- [x] アプリを起動してもコンテンツメニューにアイコンが表示されない。
- [x] "pending_shutdown.json"が生成されない
- [ ] 開発環境とリリース環境で"pending_shutdown.json"の位置を変える
- [ ] JSONファイルの書き込みや読み込みが正常に実行されているかテストする

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
