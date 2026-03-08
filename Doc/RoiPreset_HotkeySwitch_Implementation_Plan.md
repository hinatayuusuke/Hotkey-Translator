# ROI Preset Hotkey Switch Implementation Plan

## 1. 概要

固定 10 スロットの ROI プリセットを保存し、単一のアクティブ ROI をホットキーで切り替える機能を追加する。
保存形式は絶対座標ではなく正規化座標を本体とし、解像度やウインドウサイズ差に強い実装とする。

## 2. ゴール / 非ゴール

### ゴール

- 固定 10 スロットの保存済み ROI プリセットを保持できる
- 常に有効なのは 1 個の ROI プリセットだけにする
- ホットキーで ROI プリセットを次 / 前へ切り替えられる
- ROI は `NormalizedRoi` ベースで保存し、現在の capture bounds に復元できる
- 既存の F6 ROI 選択フローと共存できる
- UI のスロット選択時に、その ROI 枠線を約 1 秒プレビュー表示できる

### 非ゴール

- 複数 ROI の同時有効化
- ROI ごとの OCR エンジン設定保存
- ROI ごとの翻訳設定保存
- ゲーム別自動切替
- 複数 ROI の並列 OCR
- スロット数の可変化
- スロット名の自由編集

## 3. 前提・仮定

- 現在の設定は `AppSettings.EnableRoi`, `AppSettings.Roi`, `AppSettings.NormalizedRoi` を持つ
- 実動作では `NormalizedRoi` が優先され、必要に応じて絶対 ROI から移行される
- F6 は ROI 選択ホットキーとして既に存在する
- 現在のパイプラインは「単一 ROI」を前提にしているため、その前提は維持する

## 4. 現状整理

### 現行挙動

- ROI は 1 つだけ保存される
- ROI 選択は F6 または UI の `Select ROI` で行う
- 実行時は `NormalizedRoi` があればそこから復元し、なければ旧 `Roi` を参照する
- ROI は scene change watcher、OCR 切り出し、overlay 表示位置に影響する

### 現状の課題

- ゲームや場面ごとに ROI を毎回描き直す必要がある
- 字幕位置が複数あるタイトルでは切替コストが高い
- ROI 変更のたびに既存 1 個を上書きしてしまう

### 制約

- ROI が複数同時有効になると、OCR・overlay・scene watcher が一気に複雑化する
- ミラー / Hook / WPF で capture space の扱いが絡むため、保存は正規化座標に寄せるべき
- ユーザ体験としては「選択中スロットが今の保存先」である方が分かりやすい

## 5. 提案アーキテクチャ

### コンポーネント構成

- `Models/RoiPreset.cs`
  - 保存済み ROI スロット 1 件
- `AppSettings`
  - 固定 10 スロット配列
  - アクティブスロット index
- `RoiPresetService` または既存 settings / UI 層の補助ロジック
  - 保存、適用、切替、プレビュー表示を担当
- `HotkeyCommandController`
  - 次 / 前スロット切替の入口
- `MainWindow.xaml(.cs)`
  - 最小 UI を担当

### データフロー

1. ユーザが F6 で ROI を描画する
2. 確定時、現在の capture bounds に対する `NormalizedRoi` が得られる
3. 現在選択中のスロットへその ROI を保存する
4. スロット選択またはホットキー切替でアクティブスロットを変更する
5. 選ばれたスロットの `NormalizedRoi` を `AppSettings.NormalizedRoi` へ反映する
6. 適用時に現在 ROI の枠線を約 1 秒プレビュー表示する
6. 既存パイプラインは「現在の単一 ROI」としてそのまま利用する

### 既存パターンへの整合

- 実行系は既存の `EnableRoi + NormalizedRoi` をそのまま使う
- プリセット切替は、内部的には `AppSettings.NormalizedRoi` を差し替えるだけにする
- つまり OCR / scene change / overlay 側は ROI プリセットの存在を直接知らなくてよい

## 6. インターフェース設計

### `RoiPreset`

- `int SlotIndex`
- `NormalizedRect? NormalizedRoi`
- `bool EnableRoi`

初期実装ではこれで十分。
`Roi` の絶対座標版は保存しない。

### `AppSettings` 追加項目

- `List<RoiPreset> RoiPresets`
- `int ActiveRoiPresetIndex`

### 運用ルール

- `RoiPresets` は常に 10 件を保持する
- `ActiveRoiPresetIndex` は `0..9` を取る
- ドロップダウンで選んだスロットは即時アクティブ化する
- アクティブスロットに ROI が保存済みなら、その ROI を即時適用する
- アクティブスロットが空なら ROI は変更せず、保存先だけそのスロットへ切り替える

### ホットキー案

- `Next ROI Preset`
- `Previous ROI Preset`

MVP ではこの 2 つを推奨する。
直接 `Slot 1..10` を割り当てる案は後回しでよい。

### UI 案

- ROI セクションに `ROI Slot` ブロックを追加
- 項目:
  - `Slot 1..10` を選べる ComboBox
  - `Select ROI` は現在スロットへの保存動作として扱う
  - 選択中スロットが保存済みかどうかを表示
  - 必要なら `Clear Slot` ボタンを追加

## 7. 実装手順

### Step 1. モデル追加

- `Models/RoiPreset.cs` を追加
- `AppSettings` に `RoiPresets`, `ActiveRoiPresetIndex` を追加
- `RoiPresets` は初期化時に 10 スロットを埋める

### Step 2. 保存 / 適用ロジック

- 現在 ROI を「アクティブスロット」へ保存する処理を追加
- スロット選択時に、そのスロットの ROI を現在 ROI へ適用する処理を追加
- 空スロット選択時は保存先だけ変更する
- `Clear Slot` 時はそのスロットの `NormalizedRoi` を空にする

### Step 3. アクティブ切替ルール

- スロット適用時:
  - `Settings.NormalizedRoi = preset.NormalizedRoi`
  - `Settings.EnableRoi = preset.EnableRoi`
  - `Settings.ActiveRoiPresetIndex = selectedIndex`
- F6 ROI 確定時:
  - `Settings.ActiveRoiPresetIndex` が指すスロットへ保存する
  - 保存後、そのスロット内容を現在 ROI として適用する
- Esc キャンセル時:
  - スロット内容は変更しない

### Step 4. ホットキー追加

- `HotkeyNextRoiPresetKey`
- `HotkeyNextRoiPresetModifiers`
- `HotkeyPreviousRoiPresetKey`
- `HotkeyPreviousRoiPresetModifiers`

初期デフォルトは以下を採用する。

- `Next ROI Preset = Shift + F6`
- `Previous ROI Preset = Ctrl + F6`

既存の `F6` ROI 選択と共存し、機能群をまとめて覚えやすくする。
ホットキー切替時も選択時と同じく即時適用 + 1 秒プレビューを行う。

### Step 5. UI 最小追加

- ROI 領域へスロット選択 ComboBox を追加
- `Slot 1..10` を固定表示する
- 選択変更で即時適用する
- 適用時は ROI 枠線プレビューを約 1 秒表示する
- `Select ROI` は選択中スロットへの保存動作であることを UI 上で明示する

### Step 6. 実行時反映

- プリセット切替後は settings を保存
- 既存の ROI status message を更新
- 必要なら overlay / watcher 側へ「ROI changed」を通知する
- ROI プレビュー表示は既存 ROI preview 経路を再利用し、約 1 秒後に自動消去する

## 8. 非機能要件チェック

### 性能

- ROI プリセット数は少数想定なので性能影響は軽微
- 適用時は単なる settings 差し替えで済む
- 1 秒プレビュー表示も軽量な枠線描画だけに留める

### セキュリティ

- ローカル settings 保存のみで追加リスクはほぼない

### 可観測性

- ログ出力候補:
  - `roi_preset_saved`
  - `roi_preset_applied`
  - `roi_preset_switched`
  - `roi_preset_cleared`
  - `roi_preset_preview_shown`

### 互換性

- 既存ユーザ設定では `RoiPresets` 未設定でも、読み込み時に固定 10 スロットを補う
- 旧 `Roi` / `NormalizedRoi` はそのまま残し、プリセット未使用時の互換を保つ

### 運用

- 初期は固定 10 スロット運用
- 自動ゲーム別切替は将来拡張に回す

## 9. リスクと緩和策

- 解像度変更やウインドウ比率変化で ROI の見え方がズレる

### Mitigation

- 保存は正規化座標のみとする
- 復元時に current frame bounds へクランプする


### Risk

- 空スロットを選んだ時の挙動が分かりにくい

### Mitigation

- 「保存先だけ切り替わった」ことを UI とログで明示する
- 空スロット選択時は既存 ROI を消さず、枠線プレビューも出さない

## 10. 影響範囲

変更ファイル候補:

- `Models/AppSettings.cs`
- `Models/RoiPreset.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Services/HotkeyCommandController.cs`
- `Services/Settings/Rules/HotkeyDefaultsRule.cs`
- 必要なら ROI 関連の ViewModel / UI bridge
- ROI プレビュー消去用の軽量タイマー処理

将来的に関係する可能性のある箇所:

- `Services/PipelineOrchestrator.cs`
- `Services/SceneTextSnapshotService.cs`

ただし MVP では、これらは `NormalizedRoi` の既存読取をそのまま使う前提

## 11. Definition of Done

- 固定 10 スロット ROI プリセットを settings に保存できる
- 1 個のアクティブスロットだけを適用できる
- ホットキーで次 / 前プリセット切替ができる
- 切替後、既存 OCR / overlay / scene watcher が正しい ROI を参照する
- ROI は正規化座標で保存され、解像度差に対して破綻しにくい
- スロット選択時に保存済み ROI 枠線が約 1 秒表示される
- F6 ROI 確定時、現在選択中スロットへ保存される
