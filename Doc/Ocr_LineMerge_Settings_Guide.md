# OCR 枠の縦結合・横結合 Settings.json 運用ガイド

## 1. まず押さえる全体フロー

`OcrLineGrouper.MergeLines()` の分岐は次の順序です。

1. `EnableLineMerge=false` なら結合は一切せず、そのまま返す。
2. `VerticalModeOverride=Horizontal` なら横書き経路を強制する。
3. `VerticalModeOverride=Vertical` なら縦書き経路を強制する。
4. `VerticalModeOverride=Auto` の場合のみ自動判定に進む。
5. 自動判定は `EnableVerticalMerge=true` かつ `VerticalModeAutoDetect=true` かつ `SourceLanguage` が `ja*` / `zh*` のときだけ有効。
6. 自動判定が無効な場合、横書き経路で処理する。

NOTE: `VerticalModeOverride` が `Horizontal` / `Vertical` のときは、`EnableVerticalMerge` と `VerticalModeAutoDetect` は実質使われません。

## 2. Settings.json 項目一覧（結合関連）

### 2.1 共通ゲート

`EnableLineMerge` (default: `true`)
- 全結合機能のマスターON/OFF。

`EnableTwoStageLineMerge` (default: `true`)
- 横書き: Stage A（同一行トークン結合）+ Stage B（行同士結合）を使う。
- 縦書き: Stage A（同一列トークン結合）+ Stage B（列同士結合）を使う。
- `false` の場合:
- 横書きは Stage B 相当のみ。
- 縦書きは並び替えのみで、実質結合しない。

`VerticalModeOverride` (default: `Auto`)
- `Auto` / `Horizontal` / `Vertical`。
- 優先順位が最も高く、結合方向を決める主スイッチ。

`VerticalModeAutoDetect` (default: `true`)
- `VerticalModeOverride=Auto` のときだけ有効。

`EnableVerticalMerge` (default: `true`)
- `VerticalModeOverride=Auto` のとき、自動判定を許可するフラグ。

### 2.2 横書き Stage A（同一行トークン結合）

`RowMergeYCenterToleranceRatio` (default: `0.45`)
- 同一行クラスタ判定の Y 中心差許容。
- 上げるほど、上下にずれたトークンも同一行扱いしやすい。

`RowMergeHeightRatioMin` (default: `0.55`)
- 同一行クラスタ判定の高さ比下限。
- 下げるほど、高さ差のあるトークンを同一行扱いしやすい。

`RowMergeNeighborCount` (default: `24`)
- 近傍探索数。増やすと遠い候補も比較する。

`RowMergeMaxGapRatio` (default: `1.5`)
- 同一行内での隣接結合の X ギャップ許容。

`RowMergeHardBreakRatio` (default: `2.0`)
- この比率を超える X ギャップは強制非結合。

### 2.3 横書き Stage B（行同士の縦方向結合）

`MergeNeighborCount` (default: `12`)
- 行同士の近傍探索数。

`MergeOverlapRatioThreshold` (default: `0.1`)
- 水平方向オーバーラップの最小比率。
- 上げるほど、横位置が揃わない行は結合しにくい。

`MergeVerticalWeight` (default: `0.5`)
- 行間距離コストの重み。
- 上げるほど、縦距離が離れた行を結合しにくい。

`MergeThresholdRatio` (default: `0.9`)
- 結合許可しきい値の倍率。
- 上げるほど結合しやすく、下げるほど厳しくなる。

### 2.4 縦書き Stage A（同一列トークン結合）

`VerticalGapRatio` (default: `1.25`)
- 同一列内での Y ギャップ許容。
- 上げるほど、縦方向に離れたトークンも結合しやすい。

`VerticalColumnOrder` (default: `RightToLeft`)
- 列順と最終出力順に影響。
- `RightToLeft` は右列から左列、`LeftToRight` は左列から右列。

NOTE: 同一列クラスタ形成には固定定数（中心差・幅比・オーバーラップ最小）があり、現状 Settings.json からは直接調整できません。

### 2.5 縦書き Stage B（列同士の横方向結合）

`EnableVerticalColumnMerge` (default: `true`)
- 縦書き Stage B（列同士結合）の ON/OFF。

`VerticalColumnMergeNeighborCount` (default: `8`)
- 列同士比較の近傍探索数。

`VerticalColumnMergeOverlapRatioThreshold` (default: `0.20`)
- 縦方向オーバーラップの最小比率。
- 上げるほど、縦位置が揃わない列を結合しにくい。

`VerticalColumnMergeWeight` (default: `0.5`)
- 横ギャップのコスト重み。

`VerticalColumnMergeThresholdRatio` (default: `0.9`)
- 列結合許可しきい値の倍率。

`VerticalColumnMergeHardBreakRatio` (default: `1.8`)
- この比率を超える横ギャップは強制非結合。

## 3. よくある症状と調整方向

症状: 横書きで別段落までつながる
- `MergeThresholdRatio` を下げる。
- `MergeVerticalWeight` を上げる。
- `MergeOverlapRatioThreshold` を上げる。

症状: 横書きで同一行が分断される
- `RowMergeMaxGapRatio` を上げる。
- `RowMergeYCenterToleranceRatio` を上げる。
- `RowMergeHeightRatioMin` を下げる。

症状: 縦書きで別列までつながる
- `VerticalColumnMergeThresholdRatio` を下げる。
- `VerticalColumnMergeWeight` を上げる。
- `VerticalColumnMergeHardBreakRatio` を下げる。

症状: 縦書きで列内が分断される
- `VerticalGapRatio` を上げる。
- `EnableTwoStageLineMerge=true` を確認する。

症状: 自動判定が不安定
- 一時的に `VerticalModeOverride` を `Horizontal` または `Vertical` に固定して切り分ける。
- `Auto` を使う場合は `SourceLanguage` を `ja` / `zh` 系に合わせる。

## 4. 運用での推奨手順

1. まず `VerticalModeOverride` を固定して、横/縦それぞれで単体調整する。
2. Stage A（行内・列内）を先に調整し、その後 Stage B（行同士・列同士）を調整する。
3. 大きく触るのは1回に1～2項目までにして、ログと表示結果を比較する。
4. 最後に `VerticalModeOverride=Auto` に戻して、自動判定時の崩れがないか確認する。

## 5. 最小サンプル（Settings.json 抜粋）

```json
{
  "EnableLineMerge": true,
  "EnableTwoStageLineMerge": true,
  "VerticalModeOverride": 0,
  "VerticalModeAutoDetect": true,
  "EnableVerticalMerge": true,
  "MergeOverlapRatioThreshold": 0.1,
  "MergeVerticalWeight": 0.5,
  "MergeThresholdRatio": 0.9,
  "MergeNeighborCount": 12,
  "RowMergeYCenterToleranceRatio": 0.45,
  "RowMergeHeightRatioMin": 0.55,
  "RowMergeMaxGapRatio": 1.5,
  "RowMergeHardBreakRatio": 2.0,
  "RowMergeNeighborCount": 24,
  "VerticalColumnOrder": 0,
  "VerticalGapRatio": 1.25,
  "EnableVerticalColumnMerge": true,
  "VerticalColumnMergeNeighborCount": 8,
  "VerticalColumnMergeOverlapRatioThreshold": 0.2,
  "VerticalColumnMergeWeight": 0.5,
  "VerticalColumnMergeThresholdRatio": 0.9,
  "VerticalColumnMergeHardBreakRatio": 1.8
}
```

