# `lParam` と `Marshal.PtrToStructure` とは

対象コード: `MainWindow.xaml.cs` の `WndProc`
関連: [StructLayout(LayoutKind.Sequential)](StructLayout-Sequential.md)

```csharp
private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
    if (msg == WM_POWERBROADCAST) {
        switch (wParam.ToInt32()) {
            ...
            case PBT_POWERSETTINGCHANGE:
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
```

## `lParam` とは

Windows がウィンドウに送る**メッセージにくっついてくる、2つ目の追加情報**です。

Windows は、何かが起きるとウィンドウに「メッセージ」を送ります（クリックされた、サイズが変わった、スリープする、など）。メッセージは次の4つの値でできています。

| 値 | 中身 |
|---|---|
| `hwnd` | どのウィンドウ宛てか |
| `msg` | 何が起きたか（`WM_POWERBROADCAST` = 電源関係のできごと） |
| `wParam` | 追加情報その1 |
| `lParam` | 追加情報その2 |

`wParam` と `lParam` に何が入るかは、**メッセージの種類ごとに決まっています**。`WM_POWERBROADCAST` の場合は次のとおりです。

- `wParam`: 電源イベントの種類（`PBT_APMSUSPEND` = スリープする、`PBT_POWERSETTINGCHANGE` = 電源設定が変わった、など）
- `lParam`: `PBT_POWERSETTINGCHANGE` のときだけ、詳しい内容（`POWERBROADCAST_SETTING`）が置かれた**メモリの場所（アドレス）**。それ以外のときは使われない

型が `IntPtr` なのは、`IntPtr` が「アドレスを入れられる大きさの整数」だからです（64ビット版 Windows では8バイト）。`lParam` は、数値そのものが入るメッセージもあれば、アドレスが入るメッセージもあります。

名前の `w` と `l` は、昔の Windows で `wParam` が WORD（16ビット）、`lParam` が LONG（32ビット）だった名残です。今はどちらも `IntPtr` と同じ大きさで、名前に意味はありません。

## `Marshal.PtrToStructure` とは

**アドレスが指すメモリのバイト列を読み、C# の構造体にコピーして返す**メソッドです。

```csharp
var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
//            「lParam の場所にあるバイト列を POWERBROADCAST_SETTING の形として読んで」
```

`lParam` はただの数値（アドレス）です。C# の普通のコードでは、そのアドレスの中身を直接読むことはできません。このメソッドが、次の2つをつないでいます。

- **アンマネージドメモリ**: Windows（C 言語で書かれた部分）が管理しているメモリ。`POWERBROADCAST_SETTING` のデータはここにあります。
- **マネージドメモリ**: .NET が管理しているメモリ。C# の変数 `setting` はここにあります。

`Marshal` クラスは、この2つの世界の間でデータを受け渡す（マーシャリングする）ための機能をまとめたクラスです。

読み取るときは、構造体のフィールドの並び（`[StructLayout(LayoutKind.Sequential)]` で決めた順番）どおりにバイト列を当てはめます。そのため、C# の構造体の形と Windows 側の形が一致している必要があります。

### すぐにコピーする理由

`lParam` が指すメモリは Windows のもので、有効なのは**このメッセージを処理している間だけ**です。`WndProc` から戻ったあとは、中身が消えたり別のデータに変わったりします。`PtrToStructure` は中身を C# 側へコピーするので、コピーした `setting` はその後も安全に使えます。

## ドキュメント

- [WM_POWERBROADCAST メッセージ（Microsoft Learn）](https://learn.microsoft.com/ja-jp/windows/win32/power/wm-powerbroadcast)
- [PBT_POWERSETTINGCHANGE イベント（Microsoft Learn）](https://learn.microsoft.com/ja-jp/windows/win32/power/pbt-powersettingchange) — `lParam` が `POWERBROADCAST_SETTING` を指すことが書かれています
- [ウィンドウ プロシージャの記述（Microsoft Learn）](https://learn.microsoft.com/ja-jp/windows/win32/learnwin32/writing-the-window-procedure) — `wParam` / `lParam` の説明
- [HwndSourceHook デリゲート（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.windows.interop.hwndsourcehook) — WPF で `WndProc` を受け取る仕組み
- [Marshal.PtrToStructure メソッド（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.marshal.ptrtostructure)
- [IntPtr 構造体（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.intptr)
