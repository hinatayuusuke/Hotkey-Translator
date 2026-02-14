# PaddleOCR-VL Runtime Controls UI露出 実装案

1. **概要（1–3行）**
- `PaddleVlUseLayoutDetection` と `PaddleVlPrecision` を `Settings > PaddleOCR-VL` にUI露出し、運用時に即座に切替可能にする。
- 既存の `AppSettings` / gRPC起動引数配線は生かし、UI と `SettingsViewModel` の不足分のみを追加する。
- `PaddleVlUseLayoutDetection` は `bool?` を維持するため、`Auto/ON/OFF` の3値UIで扱う。

2. **ゴール / 非ゴール**
### ゴール
- `PaddleVlUseLayoutDetection` をUIで設定可能にする（`Auto/ON/OFF`）。
- `PaddleVlPrecision` をUIで設定可能にする（`fp16/fp32`）。
- 変更後に既存の「設定を反映して再起動」でホスト反映できる状態を維持する。

### 非ゴール
- OCRアルゴリズムやパーサー挙動の再設計。
- PaddleOCR-VLのPython実装変更。
- 既存設定ファイルの強制マイグレーション（互換破壊）。

3. **前提・仮定**
- `AppSettings` には既に `PaddleVlUseLayoutDetection`（`bool?`）と `PaddleVlPrecision`（`string?`）が存在する。
- `PaddleVlGrpcHost` は両設定を起動引数に反映済み。
- 現在のUIは `Pipeline/MaxPixels/LayoutThreshold/MaxNewTokens` まで露出済みで、対象2項目のみ未露出。

4. **現状整理**
- 露出済み（UI）
  - `PaddleVlPipelineVersion`, `PaddleVlMaxPixels`, `PaddleVlLayoutThreshold`, `PaddleVlMaxNewTokens`
- 未露出（UI）
  - `PaddleVlUseLayoutDetection`（`MainWindow.xaml` にバインド無し）
  - `PaddleVlPrecision`（`MainWindow.xaml` にバインド無し）
- `SettingsViewModel` 側も対象プロパティの保持/Load/Apply が未実装。

5. **提案アーキテクチャ**
### コンポーネント構成
- `MainWindow.xaml`
  - PaddleOCR-VLセクションに2つの入力UIを追加。
- `ViewModels/SettingsViewModel.cs`
  - 新規UIプロパティ + `LoadFrom/ApplyTo` + `On...Changed` を追加。
- `Services/Settings/Rules/PaddleOcrSettingsRule.cs`
  - `Precision` の正規化/安全補正（必要最小限）を追加。

### データフロー
1. UI (`ComboBox`) 変更。
2. `SettingsViewModel` が `RequestSaveOnValueChange()` で保存予約。
3. `ApplyTo` で `AppSettings` へ反映。
4. 既存「設定を反映して再起動」で `PaddleVlGrpcHost` 再起動。
5. `server.py` 起動引数へ反映。

### 既存パターン整合
- 既存の `Tag` ベース `ComboBox + SelectedValue` パターンに合わせる。
- 既存debounce保存フロー（`SettingsChangeScheduler`）に乗せる。

6. **インターフェース設計**
### UI項目（追加）
- `Layout detection`
  - 種別: `ComboBox`
  - 値: `Auto` / `Enable` / `Disable`
  - 内部Tag: `auto` / `true` / `false`
- `Precision`
  - 種別: `ComboBox`
  - 値: `fp16` / `fp32`
  - 内部Tag: `fp16` / `fp32`

### ViewModelプロパティ（案）
- `string PaddleVlUseLayoutDetectionModeTag`
- `string PaddleVlPrecisionTag`

### 変換規則
- `PaddleVlUseLayoutDetection` (`bool?`) <-> `PaddleVlUseLayoutDetectionModeTag`
  - `null` -> `auto`
  - `true` -> `true`
  - `false` -> `false`
- `PaddleVlPrecision` (`string?`) <-> `PaddleVlPrecisionTag`
  - `null/空` -> `fp32`（現行正規化と整合）
  - `fp16/fp32` を正規化して保持

7. **実装手順（ステップ分割）**
- Step 1: ViewModel拡張
  - `SettingsViewModel` に `PaddleVlUseLayoutDetectionModeTag`, `PaddleVlPrecisionTag` を追加。
  - `LoadFrom` で `AppSettings` からマッピング。
  - `ApplyTo` で `AppSettings` へ逆マッピング。
  - `On...Changed` で保存予約。

- Step 2: XAML UI追加
  - `MainWindow.xaml` の PaddleOCR-VL セクションへ2行追加。
  - 既存項目レイアウト幅（`Width=120`）に合わせる。
  - 補助文言: 「反映には再起動が必要」を明記。

- Step 3: 設定正規化の補強
  - `PaddleOcrSettingsRule` で `PaddleVlPrecision` の妥当値 (`fp16/fp32`) を保証。
  - `device=cpu` かつ `precision=fp16` の場合は `fp32` へ補正するかを明示決定。
    - 推奨: `fp32` 自動補正 + reportログ（運用事故を減らす）。

- Step 4: 動作確認
  - UI変更 -> 保存 -> `settings.json` 反映を確認。
  - 「設定を反映して再起動」で `PaddleVlGrpc` 起動引数が変わることをログ確認。

8. **非機能要件チェック**
- 性能
  - UI追加のみでOCR実行性能への直接影響なし。
- 可観測性
  - 既存のホスト起動ログで設定反映確認可能。
- 互換性
  - `bool?` を3値UIで保持し、既存 `null` 意味（Auto）を破壊しない。
- 運用
  - トラブル時に `Precision=fp32`, `LayoutDetection=Auto/Off` へ即戻し可能。

9. **リスクと緩和策**
- Risk: `CheckBox` 2値化で `bool?` の `null` が失われる。
- Mitigation: `Auto/ON/OFF` の `ComboBox` で3値を保持する。

- Risk: `fp16` が一部環境で不安定。
- Mitigation: 既定は現行運用値を尊重しつつ、`device=cpu` 時は `fp32` 補正を導入する。

- Risk: 変更後に「反映されない」と誤解される。
- Mitigation: UIラベル近傍に「設定を反映して再起動が必要」注記を追加する。

10. **影響範囲**
- `MainWindow.xaml` — PaddleOCR-VLセクションに `Layout detection` / `Precision` 入力を追加。
- `ViewModels/SettingsViewModel.cs` — 新規プロパティ・Load/Apply・保存トリガを追加。
- `Services/Settings/Rules/PaddleOcrSettingsRule.cs` — Precision正規化（必要ならdevice連動補正）を追加。
- `Doc/` — 本計画書（本ファイル）。

11. **Definition of Done**
- [ ] `Settings > PaddleOCR-VL` に `Layout detection`（Auto/Enable/Disable）が表示される。
- [ ] `Settings > PaddleOCR-VL` に `Precision`（fp16/fp32）が表示される。
- [ ] 設定変更後、`settings.json` に対応値が保存される。
- [ ] 「設定を反映して再起動」後、PaddleVL gRPC起動ログで反映値を確認できる。
- [ ] 既存設定（`null` 含む）を読み込んでもUIで破綻しない。
- [ ] `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` が成功する。
