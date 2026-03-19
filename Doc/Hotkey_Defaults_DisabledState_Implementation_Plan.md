# Hotkey Defaults / Disabled State Implementation Plan

## 1. 概要

ホットキー既定値の多重管理を一本化し、各ホットキーを明示的に無効化できる状態を導入する。  
無効状態の保存値は空文字ではなく `Disable` を採用し、一般的な既定値補完ルールは廃止する。  
新規既定値は `F6` から `F10` に絞り、補助系ホットキーは既定で `Disable` にする。

## 2. ゴール / 非ゴール

### ゴール
- ホットキー既定値の定義箇所を 1 か所に集約する。
- ホットキーの `Disable` 状態を正式にサポートする。
- 新規既定値を `F6` ROI、`F7` Lock、`F8` Run once、`F9` Toggle overlay、`F10` Force run に整理する。
- 補助系ホットキーは既定で `Disable` にする。

### 非ゴール
- ホットキー機能そのものの追加や、キー種別の大幅拡張は行わない。
- Win キー対応やマクロ対応は行わない。

## 3. 前提・仮定

- 現在のホットキー既定値は少なくとも以下で多重管理されている。
  - `Models/AppSettings.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `MainWindow.xaml.cs` の `BuildHotkeyConfigFromSettings()` / `HotkeyConfig.Default`
  - `Services/Settings/Rules/HotkeyDefaultsRule.cs`
- 現状の `ParseKey()` は空文字や `Key.None` を許可せず fallback に戻すため、単純に空文字を保存しても無効化にならない。
- 現状の `BuildHotkeyRegistrations()` は全バインドを常に登録対象にしているため、無効状態を導入するには登録前フィルタが必要。
- プロダクトは未リリースであり、既存ユーザー互換は考慮不要。

## 4. 現状整理

### 現行挙動
- `AppSettings` と `SettingsViewModel` に既定キー文字列がある。
- `MainWindow` 側にも fallback の既定値がある。
- `HotkeyDefaultsRule` が空文字を見つけると一部ホットキーを既定値へ戻す。
- その結果、「空文字 = 無効」が成立しない。

### 現行の問題
- 既定値変更時に複数ファイルを同期しないとズレる。
- `AppSettings` と `MainWindow` の fallback が一部食い違っており、意図しない既定値になる余地がある。
- 初回利用者に対して既定ホットキーが多く、他アプリ競合や誤爆を起こしやすい。

## 5. 提案アーキテクチャ

### コンポーネント構成
- `HotkeyDefaults` 相当の単一定義を導入する。
  - 各アクションの既定キー
  - 各アクションの既定 modifier
  - 既定で有効か無効か
- `AppSettings` は保存表現のみを持つ。
- `SettingsViewModel` は UI 表現として `Disable` を扱う。
- `MainWindow` は単一定義から `HotkeyConfig` を構築する。

### データフロー
1. 単一定義から新規 `AppSettings` の既定値を生成する。
2. `SettingsViewModel.LoadFrom()` で保存値を UI 表現へ変換する。
3. `ApplyTo()` で UI 表現を保存値へ戻す。
4. `MainWindow` で保存値を `HotkeyConfig` に変換する。
5. `BuildHotkeyRegistrations()` は無効バインドを除外して登録する。

### 既存パターンとの整合
- 設定保存は引き続き `AppSettings` に集約する。
- キー側は `Disable`、modifier 側は `None` という分離で意味を明確にする。

## 6. インターフェース設計

### 保存表現
- 保存上の無効状態は `Disable` とする。
- modifier は無効状態でも `"None"` を保持してよい。

### UI 表現
- キー選択肢の先頭に `Disable` を追加する。
- UI から `Disable` を選んだ場合、保存時も `Disable` を保持する。

### ランタイム表現
- `ParseKey()` は `Disable` を `Key.None` に変換する。
- `BuildHotkeyRegistrations()` は `Key.None` のバインドを登録リストから除外する。
- ログ表示の `FormatHotkey()` は `Key.None` を `"Disabled"` と表示する。

### 既定値ポリシー
- 有効のまま残す既定値
  - `SelectRoi` = `F6`
  - `LockCaptureWindow` = `F7`
  - `RunOnce` = `F8`
  - `ToggleOverlay` = `F9`
  - `ForceRun` = `F10`
- 既定で無効化する項目
  - `RunNextRoi`
  - `RunNextNextRoi`
  - `ForceRunNextRoi`
  - `ForceRunNextNextRoi`
  - `ForceGeminiStrict`
  - `OcrOnly`
  - `ToggleSceneAutoTranslate`
  - `UnlockCaptureWindow`
  - `ToggleMirrorFullscreen`

## 7. 実装手順

### Step 1. 既定値定義の一本化
- `HotkeyDefaults` 相当の定義を追加する。
- 各アクションの既定キー・modifier・disabled 既定を 1 か所へ寄せる。

### Step 2. 保存・UI の disabled 対応
- `SettingsViewModel` の hotkey 正規化を `Disable` を保持する形へ変更する。
- `HotkeysControl` の選択肢に `Disable` を追加する。

### Step 3. ランタイム登録の disabled 対応
- `ParseKey()` を disabled 対応へ変更する。
- `BuildHotkeyRegistrations()` で `Key.None` を除外する。
- `HotkeyController` 側の重複検査は登録対象のみに対して行う。

### Step 4. 既定値とログ文言の更新
- `AppSettings` の既定値を更新する。
- 起動時の hotkey help log を新しい既定構成へ合わせる。
- `HotkeyConfig.Default` が残るなら単一定義参照へ置き換える。

### Step 5. 補完ルールの廃止
- `HotkeyDefaultsRule` を削除する。
- 読み込み時の一般的な hotkey 既定値補完を廃止する。
- 既定値適用は新規 `AppSettings` 生成時のみに限定する。

## 8. 非機能要件チェック

### 性能
- 影響は軽微。登録対象が減るので、むしろ初期登録はわずかに軽くなる。

### セキュリティ
- セキュリティ影響は低い。

### 可観測性
- ログで disabled 状態を `"Disabled"` と明示する。
- 競合で部分登録になった場合も、どのバインドが生きているか把握しやすくする。

### 互換性
- 未リリース前提のため、既存ユーザー互換は考慮不要。
- 実装後は新しい既定ポリシーのみを前提に構成してよい。

### 運用
- 既定ホットキー競合の問い合わせが減る見込み。
- 補助ホットキーは必要なユーザーだけ手動で `Disable` から有効化する運用に寄せる。

## 9. リスクと緩和策

- Risk: `Disable` を導入しても fallback ロジックが残ると、無効設定が勝手に既定値へ戻る。
- Mitigation: `ParseKey()`、`SettingsViewModel`、既定値参照箇所を同時に変更し、`HotkeyDefaultsRule` は削除する。

- Risk: 既定値の一本化が不十分だと、新旧の既定値ズレが残る。
- Mitigation: 既定キーの参照元を 1 か所に絞り、他箇所はその参照だけにする。

- Risk: Unlock や Mirror の既定無効化で、一部操作が見つけにくくなる。
- Mitigation: UI で再割り当て可能にし、ヘルプ文言に「補助ホットキーは既定で `Disable`」と明記する。

## 10. 影響範囲

### 変更ファイル候補
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml.cs`
- `Services/Settings/Rules/HotkeyDefaultsRule.cs` （削除）
- `UI/HotkeysControl.xaml`
- `UI/HotkeysControl.xaml.cs`

### ドキュメント更新
- 必要ならユーザー向け hotkey help 文言を更新する。

### 移行
- 未リリース前提のため、ユーザー向け移行は不要。
- リポジトリ内の既定値と fallback を新ポリシーへ揃えればよい。

## 11. Definition of Done

- 既定ホットキー定義が 1 か所に集約されている。
- `Disable` を保存しても fallback で復活しない。
- `Key.None` のバインドは登録されない。
- 新規既定値が `F6` / `F7` / `F8` / `F9` / `F10` のみ有効になっている。
- 補助ホットキーは既定で `Disable` になっている。
- UI から `Disable` を選べる。
- 起動ログと更新ログに disabled 状態が正しく表示される。
- `dotnet build .\Hotkey-Translator.sln` が通る。
