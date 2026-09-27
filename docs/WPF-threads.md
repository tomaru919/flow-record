# WPF の UI スレッドとレンダリング スレッド / このアプリのコードはどこで動くか

対象コード: `MainWindow.xaml.cs`、`MonitorService.cs`

## 2つのスレッドの役割

WPF アプリには、最初から次の2つのスレッドがあります。

- **UI スレッド**: 自分で書いたコード（コンストラクタ、イベントハンドラ、`WndProc` など）が動くスレッド。ウィンドウやコントロールを作り、「何をどこに描くか」（ビジュアル ツリー）を組み立てる。`Dispatcher` はこのスレッドの仕事の順番待ち行列。
- **レンダリング スレッド**: UI スレッドが組み立てた描画情報を受け取って、DirectX で実際に画面へ描く。WPF の内部（ネイティブの描画エンジン）が使うスレッドで、**アプリのコードがここで動くことはありません**。

つまり、「アプリの開発者が意識するのは UI スレッドだけで、レンダリング スレッドは裏方」という関係です。レンダリング スレッドが別にあるので、アニメーションなどは UI スレッドが少し忙しくても滑らかに動けます。

## `MainWindow` のコードは UI スレッドで動くか

**基本的にすべて UI スレッドです。**

| コード | スレッド | 理由 |
| --- | --- | --- |
| コンストラクタ、`OnSourceInitialized`、`OnClosed`、`Window_Closing` | UI | WPF がウィンドウを作る・閉じる流れの中で呼ぶ |
| `WndProc`（`WM_POWERBROADCAST`） | UI | ウィンドウのメッセージ ループで処理される |
| `OnUserPreferenceChanged` | UI | UI スレッドで登録したので、UI スレッドに戻されて呼ばれる（[Dispatcher-Invoke.md](Dispatcher-Invoke.md) 参照） |
| `InitializeWebView`、`CoreWebView2_WebMessageReceived` の `await` 後の続き | UI | WPF では `await` の後、元の UI スレッドに戻ってくる |

最後の点は重要です。WPF の UI スレッドでは `await` の前に「戻り先」（`DispatcherSynchronizationContext`）が記録され、`await` が終わると続きの処理は UI スレッドで再開されます。なので `await` の直後に `webView.CoreWebView2.PostWebMessageAsJson(...)` のように UI を触っても問題ありません（`ConfigureAwait(false)` を付けるとこの仕組みがなくなります）。

## UI スレッド以外で動いているコード

`MainWindow` から呼ばれる `MonitorService` の中には、別スレッドで動く部分があります。

- **`Start` → `MonitoringLoop`**: `Task.Run` で起動しているので、**スレッドプール**（バックグラウンドのスレッド）で動きます。1秒ごとのアクティブウィンドウ監視はここです。UI スレッドとは別なので、`SuspendMonitoring`（UI スレッドから呼ばれる）との間で `_windowLock` による排他が必要になります（[SemaphoreSlim.md](SemaphoreSlim.md) 参照）。

逆に、別スレッドで動いていそうに見えても実は UI スレッドで動いているものもあります。

- **`ScheduleWakeConfirmation` → `ConfirmWakeAfterDelayAsync`**: `WndProc`（UI スレッド）から呼ばれ、`Task.Delay` の後は UI スレッドに戻るので、`RecordWakeAsync` も UI スレッドで動きます。待っている間は UI スレッドをふさぎません。
- **`GetRecordsJsonAsync` などの DB 読み込み**: `Microsoft.Data.Sqlite` の `OpenAsync` / `ExecuteReaderAsync` などは、名前は非同期でも**中身は同期的に実行されます**（SQLite が非同期 I/O に対応していないため）。なので、クエリの実行中は UI スレッドがふさがっています。データ量が少ないうちは気にならない程度です。

## レンダリング スレッドで動くコードはどこか

**このリポジトリの中にはありません。** レンダリング スレッドは WPF 内部専用で、C# のコードから直接そこで何かを実行する方法もありません。

さらに、このアプリの画面の中身はほとんど `WebView2` で表示しています。WebView2 の中身（フロントエンドの React / JavaScript とその描画）は、WPF のスレッドですらなく、**Microsoft Edge（WebView2）の別プロセス**で動いています。WPF のレンダリング スレッドが描いているのは、ウィンドウの枠組みや WebView2 を置く場所といった部分だけです。

まとめると次のとおりです。

```
FlowRecord.exe
├─ UI スレッド        … MainWindow のほぼ全部、MonitorService の await 後の続きや DB 読み込み
├─ レンダリング スレッド … WPF 内部。アプリのコードはなし
└─ スレッドプール      … MonitoringLoop（Task.Run）

msedgewebview2.exe（別プロセス）
└─ フロントエンドの JavaScript の実行と描画
```

## ドキュメント

- [スレッド モデル（WPF, Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/desktop/wpf/advanced/threading-model)
- [WPF のアーキテクチャ（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/desktop/wpf/advanced/wpf-architecture) — UI スレッドとレンダリング スレッドの役割分担
- [非同期の制限事項（Microsoft.Data.Sqlite, Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/standard/data/sqlite/async)
- [WebView2 のプロセス モデル（Microsoft Learn）](https://learn.microsoft.com/ja-jp/microsoft-edge/webview2/concepts/process-model)
