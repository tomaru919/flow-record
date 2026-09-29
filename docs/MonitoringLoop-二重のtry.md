# `MonitoringLoop` の二重の `try` は外せるか

対象コード: `MonitorService.cs` の `MonitoringLoop`
関連: [SemaphoreSlim](SemaphoreSlim.md)

## 結論

外せません。内側と外側で役割が違います。

```csharp
while (!token.IsCancellationRequested) {
    try {                                          // 外側: エラーが起きてもループを続ける／終了の合図で抜ける
        await _windowLock.WaitAsync(token);        // 鍵を取る
        try {                                      // 内側: 鍵を必ず返す
            ... ウィンドウの検知と DB 書き込み ...
        } finally {
            _windowLock.Release();
        }
        await Task.Delay(1000, token);             // 鍵を返してから1秒待つ
    } catch (TaskCanceledException) { break; }
      catch (Exception ex) { Debug.WriteLine(...); }
}
```

| | 役割 |
|---|---|
| 内側 `try` / `finally` | 鍵を取った範囲で何が起きても、必ず `Release()` する |
| 外側 `try` / `catch` | 例外でループ（＝監視）が止まらないようにする。キャンセルされたらループを抜ける |

## 内側の `try` を外すとどうなるか

```csharp
await _windowLock.WaitAsync(token);
... ウィンドウの検知と DB 書き込み ...   // ← ここで例外が起きると
_windowLock.Release();                  // ← この行が飛ばされる
```

例外が起きると、処理はいきなり外側の `catch` に飛びます。そのため `Release()` が呼ばれず、鍵が返されないままになります。すると次のことが起きます。

1. 次のループの `WaitAsync` が永遠に待ち続け、監視が止まる（記録されなくなる）
2. スリープ時に UI スレッドで呼ばれる `SuspendMonitoring` の `_windowLock.Wait()` も永遠に待ち、**アプリの画面ごと固まる**

今の中身（`CloseCurrentWindowAsync` などの DB 処理）は、それぞれの中で例外を `catch` しています。そのため、今すぐ例外が漏れてくるわけではありません。ただし、将来コードを変えたときに1か所でも例外が漏れると、アプリが固まります。これを防ぐ「保険」が `finally` です。

## 1つの `try` / `catch` / `finally` にまとめられないか

```csharp
try {
    await _windowLock.WaitAsync(token);
    ...
    await Task.Delay(1000, token);
} catch (TaskCanceledException) { break; }
  catch (Exception ex) { ... }
  finally { _windowLock.Release(); }   // ← 問題あり
```

この書き方には問題が2つあります。

1. **鍵を取れなかったのに返してしまう**: 終了時に `WaitAsync(token)` 自体がキャンセルで例外を出すと、鍵を取れていないのに `finally` で `Release()` が呼ばれます。最大数1の `SemaphoreSlim` を空きが1個の状態で `Release()` すると、`SemaphoreFullException` が出ます。
2. **1秒待つ間も鍵を持ち続ける**: `Task.Delay(1000)` も鍵を持ったまま実行されます。その間にスリープ通知が来ると、UI スレッドの `SuspendMonitoring` が最大1秒止まります。

今の形は「鍵を取った直後に `try` を始め、`finally` で返す」になっています。これは `SemaphoreSlim` を使うときの決まった書き方で、上の2つの問題が起きません。

## ドキュメント

- [try-finally（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/csharp/language-reference/statements/exception-handling-statements#the-try-finally-statement)
- [SemaphoreSlim クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.threading.semaphoreslim)
- [SemaphoreSlim.Release（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.threading.semaphoreslim.release) — 最大数を超えると `SemaphoreFullException` になることが書かれています
