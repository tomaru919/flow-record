# `DllImport` と `LibraryImport` の違い

対象コード: `MonitorService.cs`、`MainWindow.xaml.cs`
関連: [lParam と Marshal.PtrToStructure](lParam-PtrToStructure.md)

## 共通点

どちらも、**Windows の DLL（`user32.dll` など）に入っている C 言語の関数を、C# から呼ぶ**ための属性です。この仕組みを P/Invoke（Platform Invoke）と呼びます。

C# と C ではデータの持ち方が違うので、呼び出すときに引数や戻り値を変換する必要があります。この変換を**マーシャリング**と呼びます。2つの違いは、**この変換コードをいつ・誰が作るか**です。

## 違い

| | `DllImport` | `LibraryImport` |
|---|---|---|
| 変換コードを作るタイミング | アプリの実行中（初めて呼んだとき） | ビルドのとき |
| 作るもの | .NET ランタイム | ソースジェネレーター（ビルド中に C# コードを自動で書き足す仕組み） |
| 使える .NET | すべて | .NET 7 以降 |
| 書き方 | `static extern` | `static partial` |
| 起動・初回呼び出し | 少し遅い | 速い |
| Native AOT・トリミング | 向かない | 対応 |
| 変換コードをデバッグで見る | できない | できる（普通の C# コードなので） |

```csharp
// DllImport: 中身は実行時にランタイムが用意する
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

// LibraryImport: 中身はビルド時に自動生成される
[LibraryImport("user32.dll")]
private static partial IntPtr GetForegroundWindow();
```

## 補足: `DllImport` は属性なのに、なぜ「実行中に作る」のか

属性そのものは、ビルドのときにコンパイルされます。ただし、属性は**「メモ書き」であって、処理ではありません**。

ビルドで exe に入るのは、次の情報だけです。

- メソッドの宣言（名前、引数の型、戻り値の型）
- 「このメソッドの中身は `user32.dll` の `GetWindowText` にある」というメモ（メタデータ）

`extern` メソッドには中身がないので、変換コードはこの時点ではどこにもありません。

アプリを実行して、そのメソッドが初めて呼ばれたときに、ランタイムが次のことをします。

1. メタデータのメモを読む
2. DLL を読み込み、関数の場所を探す
3. 引数の型を見て、変換コード（IL スタブ）をその場で作る
4. それを機械語に変換して実行する

| | ビルドのとき | 実行中 |
|---|---|---|
| `DllImport` | メモを exe に記録するだけ | メモを読んで変換コードを作る |
| `LibraryImport` | 変換コードを C# で自動生成し、普通のコードと一緒にコンパイルする | できあがったコードを実行するだけ |

なお、.NET のコンパイルはもともと2段階です。ビルドで C# を IL（中間言語）に変換し、実行中に JIT コンパイラーが IL を機械語に変換します。`DllImport` の変換コードは、この IL 自体が実行中に作られる点が普通のコードと違います。

## 書き方の違いが出る理由

### `extern` と `partial`
- `extern` は「中身は外（ランタイム）にある」という意味です。
- `partial` は「宣言と中身を別々の場所に書く」という意味です。`LibraryImport` では、宣言を自分で書き、中身をソースジェネレーターが別のファイルに書きます。そのため、メソッドを入れるクラスにも `partial` が必要です（`public partial class MonitorService`）。

### `AllowUnsafeBlocks` が必要
自動生成される変換コードは、ポインター（メモリのアドレスを直接扱う機能）を使います。C# でポインターを使うコードは、`.csproj` に `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` がないとビルドできません。

`DllImport` でも同じことをしていますが、ランタイムの中で行われるので、自分のプロジェクトに設定は必要ありません。

### `bool` に `[MarshalAs]` が必要
```csharp
[LibraryImport("user32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static partial bool UnregisterPowerSettingNotification(IntPtr Handle);
```
C 側の「真偽値」には、4バイトの `BOOL` や1バイトの `bool` など、大きさの違う種類があります。

- `DllImport` は、何も書かなければ4バイトの `BOOL` として扱います（暗黙のルール）。
- `LibraryImport` は暗黙のルールを持たず、**どの種類かを必ず書かせます**。`UnmanagedType.Bool` が Windows の4バイトの `BOOL` です。

間違いに気づきにくい暗黙の変換をなくす、というのが `LibraryImport` の方針です。

## このプロジェクトで `DllImport` が残っている理由

`GetWindowText` と `GetClassName` は `DllImport` のままです。

```csharp
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
```

引数の `StringBuilder` は、**`LibraryImport` が対応していない型**だからです。`StringBuilder` を渡すと、ランタイムが裏で一時的なメモリを用意し、C 側が書き込んだ文字列をコピーして戻します。このコピーが無駄で遅いので、`LibraryImport` ではあえて使えなくしてあります。

`LibraryImport` に書き換える場合は、`char` の配列を渡す形に変えます。

```csharp
[LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
private static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int count);

var buffer = new char[256];
int length = GetWindowText(handle, buffer, buffer.Length);
string title = new string(buffer, 0, length);
```

`EntryPoint = "GetWindowTextW"` が必要になるのは、Windows の文字列を扱う関数に、末尾が `A`（ANSI 版）と `W`（Unicode 版）の2種類があるためです。`DllImport` は `CharSet.Unicode` を指定すると自動で `W` の方を探しますが、`LibraryImport` は自動で探さないので、名前をそのまま書きます。

## どちらを使うか

新しく書くなら `LibraryImport` が推奨です。VS Code が出していた `SYSLIB1054` の提案は、この置き換えをすすめるものです。ただし `DllImport` も廃止されるわけではなく、`StringBuilder` のように `LibraryImport` が対応していない型を使う場合はそのまま使えます。

## ドキュメント

- [P/Invoke のソース生成（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/standard/native-interop/pinvoke-source-generation) — `DllImport` との違いがまとまっています
- [LibraryImportAttribute クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.libraryimportattribute)
- [DllImportAttribute クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.dllimportattribute)
- [SYSLIB1054（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/fundamentals/syslib-diagnostics/syslib1050-1069)
- [プラットフォーム呼び出し (P/Invoke)（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/standard/native-interop/pinvoke)
