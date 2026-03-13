# NumberBox 単一ソース化移行計画

## 1. 概要

現在の数値入力は、`NumberBox.Value` 向けの数値プロパティと、既存保存経路で使う `...Text` 文字列プロパティを二重管理している。  
この計画では、設定の正本を数値プロパティへ統一し、文字列プロパティ依存を段階的に除去する。

## 2. ゴール / 非ゴール

### ゴール

- `SettingsViewModel` 内の数値入力項目を `int` / `double` / nullable 数値で一元管理する
- `NumberBox` は `Value` のみを扱う構成にする
- `ApplyTo()` の文字列パースを廃止し、型付きの値を直接 `AppSettings` へ反映する
- `AUTO=blank` のような「空欄に意味がある項目」は nullable 数値で表現する

### 非ゴール

- 数値入力以外の `TextBox` 項目まで一括で型付きプロパティへ置き換えること
- `WPF UI` 全体の再設計やレイアウト変更
- `AppSettings` 自体のスキーマ変更

## 3. 前提・仮定

- 数値入力対象は現在 `WPF UI` の `NumberBox` を使っている項目を優先する
- `AppSettings` 側の型は既に数値として定義されており、ViewModel 側だけが文字列経由になっている
- `NumberBox` の表示フォーマットは UI 層の責務とし、保存ロジックで文字列整形は行わない
- 一部項目は空欄許可が必要なため、`int?` / `double?` を使う

## 4. 現状整理

### 現行挙動

- 既存保存処理は `SettingsViewModel.ApplyTo()` で `...Text` を `int.TryParse` / `double.TryParse` して `AppSettings` に反映している
- `NumberBox` 導入時に `TextBox` 時代の `...Text` 設計を維持したため、`Value` と `Text` の契約が分離した
- 暫定対応として `Value` と `...Text` の相互同期を入れている

### 問題点

- 値の正本が不明確で、UI 表示・保存・初期化のどこが基準か分かりにくい
- 同期漏れやフォーマット差異で表示値と保存値がずれる余地がある
- 項目追加のたびに `Value` と `Text` を両方増やす必要があり保守性が低い

### 対象候補

- `PhashThreshold`
- `IouThreshold`
- `SceneChangeQuietWindowMs`
- `PaddleTextDetThresh`
- `PaddleTextDetBoxThresh`
- `PaddleTextDetUnclipRatio`
- `PaddleTextRecScoreThresh`
- `PaddleVlMaxPixels`
- `PaddleVlLayoutThreshold`
- `PaddleVlMaxNewTokens`

## 5. 提案アーキテクチャ

### コンポーネント構成

- `SettingsViewModel`
  - 数値項目の正本を保持する
  - `LoadFrom()` で `AppSettings` から直接代入する
  - `ApplyTo()` で `AppSettings` へ直接代入する
- XAML (`NumberBox`)
  - `Value` を数値プロパティへ直接バインドする
  - 小数桁、増分、空欄許可などの表示/入力制約を持つ
- `AppSettings`
  - 既存の数値型定義を維持する

### データフロー

1. 設定ロード時に `AppSettings` の数値を `SettingsViewModel` の数値プロパティへ直接セットする
2. `NumberBox` は `Value` を通してその数値プロパティを編集する
3. 保存時は `ApplyTo()` が数値プロパティを直接 `AppSettings` へ書き戻す
4. 空欄許可項目は `null` をそのまま保存対象へ反映する

### 既存パターンへの整合

- 既にスライダー系は数値プロパティを正本として扱っている
- この移行は、数値入力欄をスライダー系と同じ責務分担に揃える作業と見なせる

## 6. インターフェース設計

### ViewModel

- 必須値は `int` または `double`
- 空欄許可値は `int?` または `double?`
- `...Text` プロパティは段階的に削除する

### UI

- `NumberBox.Value` に `TwoWay` バインドする
- `UpdateSourceTrigger` は `PropertyChanged` を基本とする
- 桁表示は `MaxDecimalPlaces` と必要な表示ルールで制御する

### エラー・バリデーション

- `NumberBox` による入力制約を第一防衛線とする
- 業務上の最小/最大値がある項目は `ApplyTo()` で `Math.Clamp` を使って再保証する
- nullable 項目は `null` を有効値として扱い、文字列の空欄表現には戻さない

## 7. 実装手順

### Step 1. 数値項目の棚卸し

- `SettingsViewModel` 内の `...Text` で数値パースしている項目を一覧化する
- 「必須数値」と「空欄許可数値」に分類する

### Step 2. 正本プロパティの型を確定する

- 各項目の型を `int` / `double` / nullable に決める
- `AUTO=blank` のような項目は nullable へ寄せる

### Step 3. `LoadFrom()` を数値正本に統一する

- `AppSettings` から数値プロパティへ直接代入する
- `...Text` への代入を削除する

### Step 4. XAML を `Value` 前提に統一する

- 対象 `NumberBox` をすべて `Value` バインドへ揃える
- `Text` バインドを廃止する

### Step 5. `ApplyTo()` の文字列パースを廃止する

- `TryParse` ベースの更新を削除する
- 数値プロパティを直接 `settings` に反映する
- nullable 項目は `null` をそのまま渡す

### Step 6. 相互同期コードを撤去する

- `SyncNumericFieldFromText()` / `SyncTextFieldFromNumeric()` を削除する
- `On...TextChanged()` を削除する
- `...Text` プロパティが不要なら完全に除去する

### Step 7. 残存文字列項目を分離する

- `TextBox` 前提の項目だけ `...Text` を残す
- `SettingsViewModel` の責務を「数値」と「文字列」で自然に分ける

## 8. 非機能要件チェック

### 性能

- 文字列パース回数が減るため、保存時の無駄な変換が減る

### セキュリティ

- 外部入力の意味付けは変わらない
- 数値項目は UI 制約が強くなるため、文字列より誤入力耐性が上がる

### 可観測性

- 重要な設定保存ログがある場合、値のログ出力は nullable を考慮して維持する

### 互換性

- `AppSettings` の永続形式を変えなければ、設定ファイル互換は維持できる
- ViewModel 内部 API は変わるため、参照箇所の洗い出しが必要

### 運用

- 数値項目の追加時は「文字列プロパティを増やさない」を規約化すると再発を防げる

## 9. リスクと緩和策

- Risk: `null` と既定値の扱いを誤ると保存値が変わる
- Mitigation: nullable 項目ごとに「空欄時の意味」を明文化し、`ApplyTo()` にコメントを残す

- Risk: `NumberBox` の表示フォーマット変更で見た目が変わる
- Mitigation: 小数桁数は既存 UI と同じ設定を維持し、必要なら個別に `StringFormat` 相当の方針を決める

- Risk: 既存コードが `...Text` を参照している場合にコンパイルエラーや挙動差分が出る
- Mitigation: `rg "TextChanged|ThresholdText|MaxPixelsText|QuietWindowMsText"` で参照を洗い、段階的に削除する

## 10. 影響範囲

### 変更ファイル候補

- `ViewModels/SettingsViewModel.cs`
- `UI/OcrSettingsControl.xaml`
- `UI/OcrEnginesControl.xaml`
- `UI/OverlayBehaviorControl.xaml`

### 追加で確認すべきファイル

- `Models/AppSettings.cs`
- `Services/Application` 配下の設定保存呼び出し箇所

### ドキュメント更新

- 実装完了後、この計画書に「完了済み」注記を入れるか、実施記録を別文書で残す

## 11. Definition of Done

- 対象 `NumberBox` 項目に `...Text` 依存が残っていない
- `LoadFrom()` が対象項目を数値プロパティへ直接設定している
- `ApplyTo()` が対象項目を文字列パースせず直接保存している
- 相互同期ヘルパーが削除されている
- `dotnet build` が成功する
- 対象画面で、設定ロード直後から数値が正しく表示される
- 空欄許可項目で `null` 保存が維持される
