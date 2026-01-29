
---

### アルゴリズム案 B：距離スコアベースのクラスタリング (詳細版)

#### 1. 基本方針

**「単リンク法（Single-linkage clustering）」**に近い考え方を採用します。
「AとBが近い」かつ「BとCが近い」ならば、「A・B・Cは同じグループ」とみなします。これにより、数珠つなぎに段落を形成できます。

#### 2. コスト関数（距離スコア）の定義

2つの行（Rect A と Rect B）が「どれくらい離れているか」を計算する式です。
このスコア（Cost）が**閾値（Threshold）以下**であれば「結合可能」と判定します。

前提：`Rect A` が上、`Rect B` が下にあるとする（Y座標で判定）。

**各項目の定義:**

1. **VerticalGap (垂直距離):**
* `B.Top - A.Bottom` （隙間のピクセル数）。
* 負の値（重なっている）場合は 0 とする。


2. **P_align (水平位置のゲート) 【重要】:**
* 2段組みの誤結合を防ぐための重要パラメータ。
* 水平方向の重なり（Overlap）を確認する。
* **判定:** 重なり部分が「短い方の幅の 30%未満」なら、**結合不可（Hard Gate）**として処理を中断する。
* 重なっているなら結合判定の後続へ進む（Costには加算しない）。


3. **P_size (フォントサイズのペナルティ):**
* `Abs(A.Height - B.Height)`。
* 文字サイズが極端に違う（例：見出しと本文）場合、スコアを大きくして結合しにくくする。



**係数の例:**

* **w_v** (垂直重み): 1.0
* **Threshold** (閾値): `Min(A.Height, B.Height) * 0.7` （文字の高さの7割以内の隙間なら結合する）

---

#### 3. 処理フロー（実装ステップ）

計算量を抑えつつ実装するための手順です。`Union-Find`（素集合データ構造）を使うと効率的です。

1. **初期化:**
* 検出された全ての `OcrLine` を取得。
* Union-Find データ構造を初期化（最初は全行がバラバラのグループ）。


2. **ペアの総当たり（または近傍探索）:**
* 全ての行の組み合わせについてコストを計算するのは無駄なので、**「Y座標でソート」**した後、**「自分より下の近傍 N個（例: 5～10個）」**とのみ比較を行います。


3. **結合判定:**
* `Line[i]` と `Line[j]` の結合可否および **Cost** を計算。
* **結合可能 かつ Cost <= 閾値** ならば、`Union-Find.Union(i, j)` を実行（グループ化）。


4. **グループの統合:**
* Union-Find の結果から、同じ親を持つ行をリスト化します。
* グループごとに以下の処理を行い、新しい `OcrLine` を生成します。
* **Rect:** グループ内全てのRectを包含する矩形 (`Rect.Union`)。
* **Text:** Y座標順に並べ替え、改行コードで結合。




5. **出力:**
* 統合された新しい `OcrLine` のリストを返す。


---

### アルゴリズム案B：コスト関数と判定ロジック

以下は「2つの行（Rect A と Rect B）」が結合可能かを判定するための計算式です。
※ `Rect A` が上、`Rect B` が下にあると仮定します。

#### 1. 結合可否の事前チェック (Guard Clause)

コスト計算を行う前に、配置的に結合してはいけないケース（2段組みの隣の段など）を弾きます。

```text
// 変数定義
OverlapWidth = Max(0, Min(A.Right, B.Right) - Max(A.Left, B.Left))
MinLineWidth = Min(A.Width, B.Width)

// 例外処理：幅が0以下の不正な矩形は結合しない
IF MinLineWidth <= 0 THEN
    RETURN DoNotMerge

// P_align 相当の判定：ハードゲート
// 重なりが「短い方の行の幅の30%未満」なら即座に結合不可とする
IF (OverlapWidth / MinLineWidth) < 0.3 THEN
    RETURN DoNotMerge

```

#### 2. コスト計算式 (Cost Function)

事前チェックを通過した場合のみ、距離コストを計算します。
※ Rect は device pixel 前提で統一して扱う。

```text
// 変数定義
VerticalGap = Max(0, B.Top - A.Bottom) // 重なっている場合は0
P_size = Abs(A.Height - B.Height)      // フォントサイズの差分

// コスト算出
Cost = (w_v * VerticalGap) + P_size

```

#### 3. 結合判定 (Decision)

```text
Threshold = Min(A.Height, B.Height) * 0.7

IF Cost <= Threshold THEN
    Merge(A, B) // 結合する
ELSE
    DoNotMerge // 結合しない

```
