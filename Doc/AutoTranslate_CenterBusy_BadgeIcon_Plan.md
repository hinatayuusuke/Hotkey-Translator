# Auto-Translate 実行可視化（中央Busy復帰 + バッジアイコン形式）実装案

1. **概要（1–3行）**
- 自動翻訳有効時に「動いているか分からない」問題を解消するため、中央Busy表示を再有効化する。
- 右下スピナーは静音化方針を維持し、代わりにオーバーレイ右下へ軽量なバッジアイコンを表示する。
- バッジは「アイコンのみ」を原則とし、自動翻訳ON/OFFに応じて表示/非表示のみを切り替える。
- 既存の手動実行表示と衝突しないよう、表示責務を明確に分離する。

2. **ゴール / 非ゴール**
### ゴール
- 自動翻訳実行中の可観測性を回復する（中央Busy表示）。
- 対象アプリを隠さない軽量通知としてバッジアイコンを導入する。
- バッジは自動翻訳の有効/無効だけを示し、状態表現を単純化する。

### 非ゴール
- OCR/翻訳アルゴリズムや判定ロジックの変更。
- 既存フォールバック通知体系の刷新。
- モーダルダイアログによる操作ブロック。

3. **前提・仮定**
- 現在、自動翻訳時は右下スピナーを抑制している設計がある。
- 利用者は対象アプリ（ゲーム）を見ており、過剰なUIは避けたい。
- 「中央Busy表示」復帰要望と「バッジはアイコン形式」要望がある。

4. **現状整理**
- 自動翻訳時に可視フィードバックが不足すると、停止と誤認されやすい。
- 右下スピナーを戻すとノイズが増え、ゲーム表示の没入を下げる可能性がある。
- 中央Busyは可視性が高いが、常時表示だと邪魔になりやすい。

5. **提案アーキテクチャ**
### 5.1 表示責務の分離
- `中央BusyOverlay`:
  - 「実行中（OCR/翻訳中）」のみ表示。
  - 自動翻訳時も表示を有効化（復帰）。
- `バッジアイコン（オーバーレイ右下）`:
  - 自動翻訳の有効/無効のみを表示。
  - スピナーの代替として常時軽量表示（有効時のみ）。

### 5.2 バッジ状態
- `AutoOff`: 非表示
- `AutoOn`: 表示

### 5.3 バッジ表示ルール（アイコン形式）
- バッジ本体に文字は表示しない（常時テキスト禁止）。
- 色差分や形状差分で実行中/待機/劣化状態を表現しない。
- 説明文が必要な場合は `ToolTip` のみで補う。

### 5.4 既存方針との整合
- 右下スピナー抑制は維持（ノイズ削減方針を継続）。
- 中央Busyは「実行中のみ」復帰し、待機時は出さない。
- モーダルダイアログは使わない（非ブロッキング）。

6. **インターフェース設計**
### 6.1 AppSettings 追加（推奨）
- `ShowCenterBusyForAutoTranslate: bool`（default: `true`）
- `ShowAutoTranslateBadgeIcon: bool`（default: `true`）

### 6.2 UI設定項目
- OCR/Scene Change Automation 内に以下を追加:
  - `Show center busy while auto-translate is running`
  - `Show auto-translate badge icon`

### 6.3 Presenter API（案）
- `SetAutoTranslateBadgeVisible(bool visible)`
- `ClearAutoTranslateBadge()`

### 6.4 状態遷移イベント
- Auto-translate ON/OFF 切替時

7. **実装手順（ステップ分割）**
- Step 1: 設定追加
  - `AppSettings` / `SettingsViewModel` / `MainWindow.xaml` に2設定を追加。

- Step 2: 中央Busy表示の復帰
  - 自動翻訳実行時でも `SetBusyOverlay(true, ...)` を抑制しないよう制御を見直す。
  - `ShowCenterBusyForAutoTranslate` が false の場合のみ抑制。

- Step 3: バッジアイコン実装
  - `OverlayWindow` に右下バッジ用UI（Icon + ToolTip）を追加。
  - `OverlayPresenter` 経由で表示/非表示APIを提供。

- Step 4: SceneChange/RunCoordinator連携
  - auto-translate enable/disable でバッジ表示を更新。
  - run開始/終了ではバッジ状態を変更しない。

- Step 5: 回帰確認
  - 手動実行時の表示挙動が変わらないこと。
  - 自動翻訳ON時のみ中央Busy/バッジが期待どおり表示されること。

8. **非機能要件チェック**
- UX:
  - 中央Busyは実行中のみ、待機中は非表示。
  - バッジは小型固定で操作を妨げない。
- 可観測性:
  - ログに `auto_badge_visibility` 変更を残す。
- 性能:
  - バッジ更新はUIスレッドの軽量更新のみ。
- 互換性:
  - デフォルトONで要望を満たし、設定で旧体験へ戻せる。

9. **リスクと緩和策**
- Risk: 中央Busyが煩わしいと感じるユーザーがいる。
- Mitigation: 設定でOFF可能にする（defaultはON）。

- Risk: バッジが「実行中」かどうかを示さないため、情報量が不足する。
- Mitigation: 実行中は中央Busyで明示し、詳細はログで補完する。

- Risk: 表示要素が増えてUIが散らかる。
- Mitigation: スピナー抑制は維持し、バッジはアイコンのみ（テキストはToolTip）に限定。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `UI/OverlayWindow.xaml`
- `UI/OverlayWindow.xaml.cs`
- `Services/OverlayPresenter.cs`
- `Services/Application/MainWindowRunCoordinator.cs`

11. **Definition of Done**
- 自動翻訳ON + 実行中で中央Busy表示が出る（設定ON時）。
- 自動翻訳ON時にバッジアイコンが表示され、自動翻訳OFFで非表示になる。
- 右下スピナー抑制方針は維持される。
- 手動実行の表示挙動に回帰がない。
- 設定で中央Busy/バッジを個別にON/OFFできる。
