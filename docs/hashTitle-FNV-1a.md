# `hashTitle` 関数（FNV-1a ハッシュ）の解説

対象コード: `frontend/src/components/ActiveWindowChart.tsx`

```ts
const hashTitle = (title: string) => {
  let hash = 0x811c9dc5
  for (const char of title) {
    hash ^= char.codePointAt(0)!
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}
```

**ハッシュ**とは、文字列などから計算する数値のことです。同じ入力からは必ず同じ数値が出て、入力が少しでも違えば全く違う数値になるように作られています。この関数は、ウィンドウ名をパレットの色の番号に変えるために使っています。

## 1行ずつの説明

### `const hashTitle = (title: string) => {`
文字列 `title` を受け取る関数（アロー関数）を定義しています。

### `let hash = 0x811c9dc5`
ハッシュの初期値です。`0x` で始まる数は16進数で、10進数では `2166136261` です。

これは FNV-1a という計算方法で決められた値（offset basis）で、自分で選んだ値ではありません。0 から始めないのは、空文字列や短い文字列でも 0 付近の値に偏らないようにするためです。

### `for (const char of title) {`
文字列を1文字ずつ取り出して繰り返します。

`for...of` は、`"😀"` のような2つの単位でできた文字（サロゲートペア）も1文字として取り出します。`title[i]` を使うと、こうした文字が半分ずつに分かれて取り出されます。

### `hash ^= char.codePointAt(0)!`
- `char.codePointAt(0)`: その文字の番号（Unicode のコードポイント）を返します。たとえば `"C"` なら `67` です。
  - `!` は「ここで `undefined` にはならない」と TypeScript に伝える記号です。1文字以上ある文字列の先頭は必ずあるので、問題ありません。
- `^=`: 排他的論理和（XOR）です。2進数の各桁を比べて、違えば 1、同じなら 0 にします。これで文字の情報をハッシュに混ぜ込みます。

### `hash = Math.imul(hash, 0x01000193)`
ハッシュに `0x01000193`（10進数で `16777619`、FNV prime と呼ばれる決まった値）を掛けます。掛け算をすると、入れた文字の影響がハッシュ全体の桁に広がります。そのため、1文字違うだけで結果が大きく変わります。

普通の `*` ではなく `Math.imul` を使うのは、次の理由です。
- JavaScript の数値は小数（64ビット浮動小数点数）で、正確に表せる整数は約 9×10¹⁵ までです。
- 約 21 億 × 約 1677 万のような掛け算は、この範囲を超えて下の桁が不正確になります。
- `Math.imul` は、C 言語などと同じ「32ビット整数の掛け算」をします。はみ出た上の桁は捨てられるので、結果はいつも正確です。

### `}`
次の文字へ進みます。「XOR → 掛け算」を文字の数だけ繰り返します。

FNV-1a の「a」は、掛け算より先に XOR をする順番を表しています。逆の順番のものは FNV-1 と呼ばれます。

### `return hash >>> 0`
`Math.imul` の結果は、符号付き32ビット整数（-2147483648 〜 2147483647）です。マイナスになることがあります。

`>>> 0` は「0ビット右にずらす」操作です。何もずらさずに、符号なし32ビット整数（0 〜 4294967295）として読み直す効果があります。

マイナスのままだと、呼び出し側の `hash % PALETTE.length` もマイナスになり、`PALETTE[-3]` のような存在しない番号を指してしまいます。これを防いでいます。

## 実際の計算例（`"Code"`）

| 文字 | コードポイント | XOR 後 | `Math.imul` 後 | `>>> 0` した値 |
|---|---|---|---|---|
| （初期値） | | | 2166136261 | |
| `C` | 67 | 2166136198 | -972293646 | 3322673650 |
| `o` | 111 | 3322673565 | 1641673255 | 1641673255 |
| `d` | 100 | 1641673283 | 1293442937 | 1293442937 |
| `e` | 101 | 1293442844 | 2036185364 | 2036185364 |

- 結果は `2036185364` です。`% 9`（パレットは9色）で `2` になるので、`PALETTE[2]`（黄）が第1候補になります。
- `"C"` のあとにマイナスの値が出ていますが、次の XOR や掛け算は32ビットとして計算されるので、そのままで問題ありません。
- 最後の1文字だけ違う `"Codf"` だと `2086518221` になり、値が大きく変わります。

## ドキュメント

- [Math.imul()（MDN）](https://developer.mozilla.org/ja/docs/Web/JavaScript/Reference/Global_Objects/Math/imul)
- [String.prototype.codePointAt()（MDN）](https://developer.mozilla.org/ja/docs/Web/JavaScript/Reference/Global_Objects/String/codePointAt)
- [ビット排他的論理和代入 `^=`（MDN）](https://developer.mozilla.org/ja/docs/Web/JavaScript/Reference/Operators/Bitwise_XOR_assignment)
- [符号なし右シフト `>>>`（MDN）](https://developer.mozilla.org/ja/docs/Web/JavaScript/Reference/Operators/Unsigned_right_shift)
- [for...of（MDN）](https://developer.mozilla.org/ja/docs/Web/JavaScript/Reference/Statements/for...of)
- FNV ハッシュそのものについては「Fowler–Noll–Vo hash function」で検索すると、考案者のページや Wikipedia が見つかります。
