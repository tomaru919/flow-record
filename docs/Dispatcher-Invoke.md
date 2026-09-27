# なぜ `ApplyTitleBarTheme` は `Dispatcher.Invoke` の中で呼ばれるのか

対象コード: `MainWindow.xaml.cs` の `OnUserPreferenceChanged`

```csharp
private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
    if (e.Category == UserPreferenceCategory.General) {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Dispatcher.Invoke(() => ApplyTitleBarTheme(hwnd));
    }
}
```

## 結論

**`SystemEvents` のイベントは UI スレッド以外から届く可能性がある**、とされているので、念のため UI スレッドに処理を戻してから実行しています。ただし、このアプリの今の書き方では実際には UI スレッドで届くので、`Dispatcher.Invoke` はなくても動きます（防御的な書き方）。

## 前提: WPF の「スレッドの持ち主」ルール

WPF のウィンドウやコントロールは、**作ったスレッド（UI スレッド）からしか触ってはいけない**というルールがあります。別のスレッドから触ると `InvalidOperationException`（「別のスレッドがこのオブジェクトを所有しているため…」）になります。

`Dispatcher.Invoke(処理)` は「この処理を UI スレッドで実行して、終わるまで待つ」という命令です。別スレッドにいるときに UI を触るための定番の方法です。すでに UI スレッドにいる場合は、その場でそのまま実行されるだけなので害はありません。

## `SystemEvents` はどのスレッドでイベントを出すのか

`SystemEvents.UserPreferenceChanged` は、Windows のテーマ変更などを知らせるイベントです。内部では専用の隠しウィンドウで OS からのメッセージを受け取っていて、その隠しウィンドウが**別スレッドに作られることがあります**。Microsoft のドキュメントにも「イベントはアプリのメインスレッドとは別のスレッドで発生することがある」という趣旨の注意があります。

そのため「`SystemEvents` のハンドラから UI を触るときは Dispatcher 経由にする」というのが一般的な作法になっており、このコードもそれに従っています。

## 実際にはどうなっているか

.NET の `SystemEvents` は、ハンドラを登録したときのスレッドの `SynchronizationContext`（WPF なら「UI スレッドに戻すための仕組み」）を覚えておき、イベント発生時にはそれを使って**登録元のスレッドでハンドラを呼び出します**。

このアプリでは、`MainWindow` のコンストラクタ（UI スレッド）で登録しています。

```csharp
SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
```

なので `OnUserPreferenceChanged` は UI スレッドで呼ばれ、`Dispatcher.Invoke` は「すでに UI スレッドなのでその場で実行」になります。つまり今は保険として効いているだけです。

将来、登録する場所をバックグラウンドのスレッドに移した場合などに、この保険が効いてきます。

## 補足

- `DwmSetWindowAttribute` 自体は Win32 API なので、実は別スレッドから呼んでも動きます。守っているのは主に「WPF のオブジェクトに触る処理は UI スレッドで」という WPF 側のルールです。
- 厳密に言うと、`new WindowInteropHelper(this).Handle` も `this`（ウィンドウ）を触っているので、本気で別スレッドを想定するならこの行も `Dispatcher.Invoke` の中に入れるほうが一貫しています。
- `OnSourceInitialized` からの呼び出し（`ApplyTitleBarTheme(hwnd)`）は、もともと UI スレッドで実行されるので `Dispatcher.Invoke` は付いていません。

## Q. UI スレッドで実行されているなら、`Dispatcher.Invoke` は外してもいいか

**外しても問題ありません。**

```csharp
if (hwnd != IntPtr.Zero) ApplyTitleBarTheme(hwnd);
```

外しても安全なのは、次の条件がそろっているからです。

- `SystemEvents` は、登録したときのスレッドの `SynchronizationContext.Current` を覚えておき、そこでハンドラを呼ぶ
- 登録しているのは `MainWindow` のコンストラクタで、そのコンストラクタは `App.OnStartup`（`App.xaml.cs`）から呼ばれている
- `OnStartup` は WPF の `Dispatcher` が動き始めた後に UI スレッドで呼ばれるので、この時点で `SynchronizationContext.Current` は UI スレッドに戻すためのもの（`DispatcherSynchronizationContext`）になっている

注意点は、**登録する場所を UI スレッド以外に移すと、この前提が崩れる**ことです。登録する場所をまとめて変えるのでなければ、外すかどうかは好みの問題です。残しておいても、UI スレッドから呼ばれた `Dispatcher.Invoke` はその場で実行されるだけなので、害やコストはほぼありません。

## Q. `Dispatcher.Invoke` は本来 `MonitorService.Start` の `Task.Run` の中で使うようなものか

**「`Task.Run` の中で必ず使うもの」ではありません。「UI スレッド以外から UI のオブジェクトに触りたいときに使うもの」です。**

`MonitoringLoop` はスレッドプールで動いていますが、触っているのは Win32 API（`GetForegroundWindow` など）、SQLite、`MonitorService` 自身のフィールドだけです。WPF のウィンドウやコントロールには触っていないので、`Dispatcher.Invoke` は要りません（フィールドの同時書き換えは `_windowLock` で防いでいます）。

もし将来、ループの中から画面を更新したくなった場合（例: ウィンドウが切り替わったら WebView にすぐ通知する）は、そこで初めて必要になります。

```csharp
// スレッドプールから WebView2 を触る場合（例）
Application.Current.Dispatcher.InvokeAsync(() => {
    mainWindow.RequestRefresh();
});
```

そのときのポイントは次の3つです。

- **`Invoke` より `InvokeAsync`（または `BeginInvoke`）を選ぶ。** `Invoke` は UI スレッドの処理が終わるまで呼び出し側を止めます。UI スレッドが逆にバックグラウンド側の完了を `.Wait()` などで待っていると、お互いに待ち合って止まります（デッドロック）。
- **`MonitorService` に `Dispatcher` を直接持たせない。** `MonitorService` は画面と関係のない記録係なので、`WindowChanged` のようなイベントを用意し、受け取った `MainWindow` 側で UI スレッドに戻すほうが、役割分担がきれいになります。
- `Task.Run` を使わずに UI スレッドから `async` メソッドを呼べば、`await` の後は自動で UI スレッドに戻るので、`Dispatcher` は要りません（[WPF-threads.md](WPF-threads.md) 参照）。

## ドキュメント

- [SystemEvents クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/microsoft.win32.systemevents)
- [スレッド モデル（WPF, Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/desktop/wpf/advanced/threading-model)
- [Dispatcher.Invoke メソッド（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.windows.threading.dispatcher.invoke)
