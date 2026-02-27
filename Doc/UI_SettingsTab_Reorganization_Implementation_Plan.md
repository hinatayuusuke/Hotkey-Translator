# UI Settings Tab Reorganization Implementation Plan

## 1. 概要
- 設定UIを「役割単位」で再編成し、OCR/Hook/自動翻訳/エンジン設定の混在を解消する。
- Homeタブは維持し、`Overlay Layout` を Home 下部の通常ブロックとして追加する（Expanderは使わない）。
- 既存設定キー・保存形式は変更せず、項目の配置のみ再編成して回帰リスクを抑える。
- UIラベル（Tab/Header/Section）はすべて英語に統一する。

## 2. ゴール / 非ゴール
### ゴール
- Homeタブを維持したまま、`Overlay Layout` を Home 下部へ移動する。
- Homeの翻訳プロバイダ簡易トグル行を `Enable DeepL / Enable Gemini / Enable LlamaCpp` の横並びに統一する。
- Settings内に以下カテゴリを用意する。
  - `OCR Settings`（旧OCRカテゴリを改名）
  - `OCR Engines`（Paddle系設定を集約）
  - `Auto Translate`（Scene Change / Quiet Window関連を集約）
  - `Hook`（DX11 Hook + Mirror Fullscreen関連）
  - `Translation`（既存翻訳エンジン設定）
  - `Hotkeys`（既存ホットキー設定）
- `OCR Tuning` セクション名を採用する。

### 非ゴール
- 設定キー名・型・デフォルト値の変更。
- SceneChange判定ロジック、Hook描画ロジック、OCRエンジン内部ロジックの変更。
- 新機能追加（UI再配置以外）。

## 3. 前提・仮定
- 現在のSettingsカテゴリは `SelectedSettingsCategoryIndex` により4カテゴリで切替される。
- Hook/Mirror排他は `SettingsViewModel` の既存ロジックで動作しているため、UI移動後も同ロジックを再利用する。
- バインディングは既存 `SettingsViewModel` プロパティをそのまま使う。

## 4. 現状整理
- Homeタブに `Overlay`（base font / opacity）がある。
- SettingsのOCRカテゴリに以下が混在している。
  - OCR前処理
  - Scene Change / Quiet Window
  - DX11 Hook
  - Mirror Fullscreen
  - OCR Input
  - Performance / Logging
  - Overlay Layout
- Paddle系設定は別カテゴリ (`SettingsPanelPaddle`) に存在する。

## 5. 提案アーキテクチャ
### 5.1 カテゴリ構成（新）
1. `OCR Settings`
- `OCR Tuning`（旧 `OCR(Advanced)` を改名）
- OCR前処理（Binarization, Two-pass, Gamma）
- OCR Input（Downsample等）

2. `OCR Engines`
- 既存 `SettingsPanelPaddle` の内容を移動
- PaddleOCR Core + PaddleOCR-VL + Runtime control

3. `Auto Translate`
- `EnableSceneChangeAutoHide`
- `EnableSceneChangeAutoTranslate`
- Quiet Window関連
- Watcherしきい値関連（threshold / interval / watcher pHash）
- Text-weighted scoring

4. `Hook`
- `EnableDx11HookPipeline`
- `Dx11HookOverlayEnabled`
- `Dx11HookFallbackOnError`
- `Dx11HookCaptureFpsLimit`
- Mirror Fullscreen（Enable/profile/core path）

5. `Translation`
- 既存Translation詳細設定を維持（`Enable LlamaCpp` トグルはHomeへ移設）

6. `Hotkeys`
- 既存Hotkeys設定を維持

### 5.2 Homeタブ
- Home下部に `Overlay Layout` ブロックを追加（Expander化しない）。
- Translationブロックの簡易トグルを次の3項目で横並びにする。
  - `Enable DeepL`
  - `Enable Gemini`
  - `Enable LlamaCpp`
- 移動対象:
  - `EnableFixedRoiOverlay`
  - `EnableOverlayFontStabilization`
  - `EnableSmallBoxReadabilityBoost`
  - `SmallTextThresholdPx`

## 6. インターフェース設計
### 6.1 ViewModel
- `SelectedSettingsCategoryIndex` のカテゴリ番号を6カテゴリへ拡張する。
- `SelectedSidebarIndex` との同期マッピングを更新する。
- 既存プロパティ/保存処理は変更しない。

### 6.2 XAML
- `SettingsCategoryList` を6項目へ更新（英語ラベル固定）。
- `SettingsCategoryList` の想定表示順:
  - `OCR Settings`
  - `OCR Engines`
  - `Auto Translate`
  - `Hook`
  - `Translation`
  - `Hotkeys`
- `SettingsPanelOcr` を `SettingsPanelOcrConfig` に改名（x:Name変更は任意、見通し優先で推奨）。
- `SettingsPanelPaddle` を `SettingsPanelOcrEngine` として表示カテゴリを更新。
- `SettingsPanelSceneAutoTranslate`（新規）を追加。
- `SettingsPanelHook`（新規）を追加。
- Homeへ `Overlay Layout` ブロックを追加し、Settings側の同ブロックは削除。
- HomeのTranslation簡易トグル行に `Enable LlamaCpp` を追加し、Translation詳細側の同トグルは削除する。

## 7. 実装手順（ステップ分割）
### Step 1: カテゴリ定義の再構成
- `MainWindow.xaml` の `SettingsCategoryList` を6カテゴリ化。
- 各 `SettingsPanel*` の `Visibility` (ConverterParameter) を再割当。

### Step 2: OCR Settingsカテゴリ整備
- 旧OCRカテゴリから Hook / Mirror / Scene / Performance / Overlay Layout を除去。
- `OCR Tuning` セクション見出しへ改名する。

### Step 3: OCR Enginesカテゴリ整備
- 既存Paddleカテゴリを `OCR Engines` 名称で再配置。
- Paddle Core + VL + runtime controlを維持。

### Step 4: Auto Translateカテゴリ新設
- Scene Change / Quiet Window / Watcher関連UIを移設。
- 既存バインディング維持（設定キー変更なし）。

### Step 5: Hookカテゴリ新設
- Hook + Mirror設定UIを移設。
- 排他の動作確認（既存ViewModelロジックを再利用）。

### Step 6: HomeへのOverlay Layout移動
- Homeタブ末尾に `Overlay Layout` を通常ブロックとして追加。
- Settings側の重複ブロックを削除。

### Step 7: LlamaCpp toggleのHome移設
- `Enable LlamaCpp` をHomeの `Enable DeepL / Enable Gemini` 行へ追加し、横並びに統一する。
- Translation詳細セクションから `Enable LlamaCpp` の重複トグルを削除する（設定キーは同一のまま）。

### Step 8: Sidebar同期の更新
- `MainWindowViewModel` の `OnSelectedSidebarIndexChanged` / `OnSelectedSettingsCategoryIndexChanged` のマッピング更新。
- サイドバー文言は英語で統一し、必要最小限でマップする（例: `Home / Translation / OCR / Hotkeys / System`）。

## 8. 非機能要件チェック
- 互換性: 既存 `AppSettings` JSON読み書き互換を維持。
- 可観測性: ログ仕様変更なし（UI再配置のみ）。
- 品質: バインディング切れ、IsEnabled条件、MultiBindingの回帰を重点確認。

## 9. リスクと緩和策
- Risk: XAML移設時のバインディング切れ（x:Name参照、ElementName依存）。
- Mitigation: `dotnet build` に加え、主要トグル操作の手動確認を実施。

- Risk: カテゴリindex変更によるSidebar遷移不整合。
- Mitigation: `MainWindowViewModel` の相互変換ロジックを同時更新し、一覧テストする。

- Risk: 同一項目の重複配置による設定混乱。
- Mitigation: 移設後に重複項目を削除し、UI上は単一配置に統一する。

## 10. 影響範囲（変更候補）
- `MainWindow.xaml`
- `ViewModels/MainWindowViewModel.cs`
- （必要時のみ）`MainWindow.xaml.cs` の初期タブ選択補助ロジック

## 11. Definition of Done
- [ ] Homeタブに `Overlay Layout` が表示される（Expander未使用）。
- [ ] HomeのTranslation簡易トグルが `Enable DeepL / Enable Gemini / Enable LlamaCpp` の横並びで表示される。
- [ ] Settingsに `OCR Settings / OCR Engines / Auto Translate / Hook / Translation / Hotkeys` が表示される。
- [ ] `OCR Tuning` 見出しが表示される。
- [ ] Hook/Mirror排他が従来どおり機能する。
- [ ] 設定保存・再起動後に値が保持される。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
