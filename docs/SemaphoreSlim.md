# SemaphoreSlim とは / なぜ排他が必要なのか

対象コード: `FlowRecord/MonitorService.cs`

## `SemaphoreSlim` とは

「同時に入れる人数を制限する仕組み」です。`new(1, 1)` は「最初の空き1個、最大1個」という意味で、実質的に**1人しか入れない鍵（排他ロック）**になります。

```csharp
private readonly SemaphoreSlim _windowLock = new(1, 1);
```

- `Wait()` / `WaitAsync()`: 鍵を取る。既に誰かが持っていれば、空くまで待つ。
- `Release()`: 鍵を返す。

C# には `lock` という構文もありますが、このコードでは使えない理由があります。`lock` の中では `await` が書けません（コンパイルエラーになります）。監視ループはロックの中で DB 書き込みを `await` しているので、`await` をまたげる `SemaphoreSlim` が必要になります。`SemaphoreSlim` は「取ったスレッドと返すスレッドが違ってもよい」ので、`await` の前後でスレッドが入れ替わる非同期処理と相性が良いのです。

```csharp
await _windowLock.WaitAsync(token);
try {
    // ここで await を使った DB 書き込みをしても問題ない
} finally {
    _windowLock.Release();   // 例外が起きても必ず鍵を返すため finally に書く
}
```

## なぜそれぞれに必要なのか

このコードには目的の違う鍵が2つあります。

### `_windowLock`（`MonitoringLoop` と `SuspendMonitoring`）

守っているのは `currentWindow` と `_currentWindowRecordId` です。この2つを、**別々のスレッドが同時に書き換えようとする**ため必要です。

- `MonitoringLoop`: バックグラウンドのスレッドプール上で1秒ごとに動く
- `SuspendMonitoring`: スリープ通知を受けた UI スレッドから呼ばれる

鍵がないと、たとえば次のような割り込みが起こりえます。

1. ループが「ウィンドウが変わった」と判断し、`_currentWindowRecordId` の行を閉じようとする
2. その途中でスリープ通知が入り、`SuspendMonitoring` が同じ行を閉じて `_currentWindowRecordId = null` にする
3. ループが処理を再開し、既に `null` になった状態で新しい行を作る、あるいは古い ID を使って書き込む

結果として、行が閉じられないまま残ったり、`end_time` が意図しない時刻になったりします。鍵があれば、どちらか一方の処理が終わるまでもう一方が待つので、この割り込みが起きません。

### `_shutdownLock`（`RecordShutdownAsync`）

守っているのは「`shutdown_time` を二重に書かないこと」です。このメソッドは次の流れになっています。

1. `_shutdownRecorded` を確認する（まだ記録していないか）
2. `boot_shutdown` の最新 ID を取得する
3. `UPDATE` で `shutdown_time` を書く
4. `_shutdownRecorded = true` にする

「確認してから書く」までの間に別の呼び出しが割り込むと、両方とも「まだ記録していない」と判断して二重に書き込んでしまいます。鍵を取ってから中でもう一度 `_shutdownRecorded` を確認している（二重チェック）のは、待っている間に先客が記録を終えているかもしれないためです。

```csharp
if (_shutdownRecorded) return;        // 鍵を取る前の早期リターン（軽い確認）
await _shutdownLock.WaitAsync();
try {
    if (_shutdownRecorded) return;    // 待っている間に他が記録したかもしれないので再確認
    ...
```

## 補足

この2つの鍵は別々のデータを守っているので、互いに待ち合って止まる（デッドロック）ことはありません。

ただし、保護が不完全な箇所が2つあります。

- `RecordShutdownAndStopAsync`（Exit ボタン）は `_windowLock` を取らずに `currentWindow` を触っています。
- `RecordShutdownSync`（OS シャットダウン時）は `_shutdownLock` を取らずに `shutdown_time` を書いています。こちらは、強制終了までの猶予が短く、待たされるわけにはいかないという事情もあります。

どちらも、`UPDATE ... WHERE ... IS NULL` の条件で二重書き込みの実害は防がれており、最悪でも終了間際の数秒の記録が欠ける程度です。

## ドキュメント

- [SemaphoreSlim クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.threading.semaphoreslim)
- [lock ステートメント（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/csharp/language-reference/statements/lock) — `lock` の中で `await` が使えないことも書かれています
- [try-finally（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/csharp/language-reference/statements/exception-handling-statements#the-try-finally-statement)
