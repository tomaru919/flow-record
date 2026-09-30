# `[StructLayout(LayoutKind.Sequential)]` とは / なぜ付けるのか

対象コード: `MainWindow.xaml.cs`

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct POWERBROADCAST_SETTING {
    public Guid PowerSetting;
    public uint DataLength;
    public byte Data;
}
```

## どんな属性か

構造体のフィールドを、**メモリ上にどの順番・どの位置で並べるか**を指定する属性です。

| 値 | 意味 |
|---|---|
| `LayoutKind.Sequential` | 書いた順番どおりに並べる |
| `LayoutKind.Explicit` | `[FieldOffset(n)]` で各フィールドの位置（先頭から何バイト目か）を自分で指定する |
| `LayoutKind.Auto` | .NET が効率のよい順番に自由に並べ替える（外からは位置がわからない） |

## なぜ必要なのか

この構造体は C# で作るデータではありません。**Windows が作ったデータを読むための「型紙」**です。

1. スリープや画面のオン・オフが起きると、Windows は `WM_POWERBROADCAST` メッセージを送ってきます。
2. `lParam` には、Windows がメモリ上に作った `POWERBROADCAST_SETTING` のデータの**場所（アドレス）**が入っています。
3. `Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam)` は、その場所のバイト列を、C# の構造体の形に当てはめてコピーします。

Windows 側（C 言語）の定義は次のとおりです。

```c
typedef struct {
  GUID  PowerSetting;   // 先頭から 0 バイト目、16 バイト
  DWORD DataLength;     // 16 バイト目、4 バイト
  UCHAR Data[1];        // 20 バイト目
} POWERBROADCAST_SETTING;
```

C の構造体は、書いた順番どおりにメモリへ並びます。C# 側も同じ順番・同じ位置で並んでいないと、読み取りがずれます。たとえば `Data` を読んだつもりで、`GUID` の途中のバイトを読んでしまいます。`Sequential` は「C と同じく書いた順に並べる」という約束で、Windows とデータの形を合わせるために付けています。

```
メモリ（lParam が指す場所）
┌──────────────── 16 バイト ────────────────┬─ 4 バイト ─┬ 1 バイト ┐
│ PowerSetting (GUID)                       │ DataLength │ Data     │
└───────────────────────────────────────────┴────────────┴──────────┘
0                                           16           20
```

## 補足: 何を Windows に合わせる必要があるのか

合わせる必要があるのは、**フィールドの順番と、それぞれの型の大きさ**だけです。Windows のヘッダーファイル `winuser.h` で決められている形に、次のように対応させています。

| Windows（C 言語）の型 | C# の型 | 大きさ |
|---|---|---|
| `GUID` | `Guid` | 16 バイト |
| `DWORD` | `uint` | 4 バイト |
| `UCHAR` | `byte` | 1 バイト |

名前は合わせなくても動きます。`PtrToStructure` は、先頭から何バイト目かという位置だけでデータを読むからです。構造体名の `POWERBROADCAST_SETTING` もフィールド名も自由に付けられますが、Windows のドキュメントと照らし合わせやすいように同じ名前にしています。

一方、型を間違えると位置がずれます。たとえば `DataLength` を `ulong`（8 バイト）にすると、`Data` の位置が 20 バイト目から 24 バイト目にずれ、別のバイトを読んでしまいます。

## 補足: 実は付けなくても同じ動きをする

C# のコンパイラは、`struct` に何も指定しなければ**自動で `Sequential` を付けます**。なので、この属性を消しても動きは変わりません。

それでも付けておく理由は、次の2つです。

- **Windows とデータの形を合わせる必要がある構造体だ、とコードを読む人にわかる**: 「フィールドの順番を入れ替えてはいけない」という意味の目印になります。
- **`class` に書き換えたときも壊れない**: `class` の既定値は `Auto` です。`PtrToStructure` は、`Auto` のクラスを受け付けず例外を出します。

## 補足: `Data` が `byte` 1個なのはなぜか

C 側の `UCHAR Data[1]` は「ここから `DataLength` バイトのデータが続く」という意味の書き方で、実際の長さは通知の種類によって変わります。

このアプリで受け取る2つの通知（away mode と画面の状態）は、値が 0・1・2 だけです。値の本体は4バイトの整数ですが、Windows（x86/x64）は数値を小さい桁から先に並べる（リトルエンディアン）ので、先頭の1バイトだけ読めば値がわかります。そのため `byte` 1個で足りています。

## ドキュメント

- [StructLayoutAttribute クラス（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.structlayoutattribute) — C# が構造体に既定で `Sequential` を使うことも書かれています
- [LayoutKind 列挙型（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.layoutkind)
- [Marshal.PtrToStructure メソッド（Microsoft Learn）](https://learn.microsoft.com/ja-jp/dotnet/api/system.runtime.interopservices.marshal.ptrtostructure)
- [POWERBROADCAST_SETTING 構造体（Microsoft Learn）](https://learn.microsoft.com/ja-jp/windows/win32/api/winuser/ns-winuser-powerbroadcast_setting)
