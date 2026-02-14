# Scene Change Force-Tick Auto-Translate 実装案

1. **概要（1–3行）**
- ノベルゲーム向けの最終手段として、`Watch Interval` ごとに自動翻訳実行を強制するモードを追加する。
- 本モード有効時は Stage A/B/Quiet Window 判定をバイパスし、定期的に `RunOnce` を起動する。
- 既存の自動モードを置き換えるのではなく、明示的に切り替える運用モードとして実装する。

2. **ゴール / 非ゴール**
### ゴール
- Stage A/B/Quiet Window でも取りこぼすケースで、定期実行により字幕更新を取りこぼしにくくする。
- UIから明示的に ON/OFF でき、通常モードへ容易に戻せる設計にする。
- 既存 `IsRunning` / pending / cooldown の安全策は維持する。

### 非ゴール
- OCR/翻訳品質そのものの改善。
- Stage A/B ロジックの削除や恒久置換。
- 手動実行（F8/F10）挙動の変更。

3. **前提・仮定**
- Quiet Window 方式を導入済み（または導入予定）で、通常はそれを推奨する。
- 本モードは「最終手段」であり、常用は推奨しない。
- auto-hide / auto-translate の既存排他仕様は維持する。

4. **現状整理**
- 現在は Stage A（pHash）→ Stage B（semantic）→ Quiet Window（任意）で発火する。
- ノベルの文字送り速度・演出次第では、判定ループで取りこぼしが残るケースがある。
- 最終手段としては「判定を捨てて定期実行」が最も単純かつ確実。

5. **提案アーキテクチャ**
### 5.1 新モード定義
- `SceneChangeAutoTranslateMode`（enum）を追加。
  - `SemanticGate`（既定）
  - `ForceTick`

### 5.2 ForceTick挙動
- `ForceTick` 時は watcher tick ごとに以下を評価:
  - `EnableSceneChangeAutoTranslate=true` か
  - `IsRunning` でないか
  - cooldown（既存 `WatchInterval`）を満たすか
- 条件成立で `_runOnceAsync(AutoSceneChangeRunOptions, semanticPayload: null)` を実行。
- Stage A/B/QuietWindow/pending payload は利用しない（またはスキップ）。

### 5.3 既存モードとの関係
- `SemanticGate` 時: 現行ロジック（Stage A/B + Quiet）を使用。
- `ForceTick` 時: 判定ロジックをバイパスして定期実行。

6. **インターフェース設計**
### 6.1 AppSettings
- `SceneChangeAutoTranslateMode: string`（default: `SemanticGate`）
  - 許容値: `SemanticGate`, `ForceTick`

### 6.2 UI（OCR > Scene Change Automation）
- `Auto-translate mode`（ComboBox）
  - `Semantic gate (Recommended)`
  - `Force every watch tick`
- `ForceTick` 選択時のUI制御:
  - `EnableSceneChangeSemanticGate` を無効表示
  - `EnableSceneChangeQuietWindow` を無効表示
  - 注意文を表示:
    - `NOTE: Higher CPU/API usage. Use only if semantic mode misses updates.`

### 6.3 バリデーション
- mode が未知値なら `SemanticGate` へフォールバック。
- `ForceTick` 選択時、`SceneChangeWatchIntervalMs` 下限を `500` 推奨（必要なら強制clamp）。

7. **実装手順（ステップ分割）**
- Step 1: 設定モデル追加
  - `AppSettings` / `SettingsViewModel` / 保存読込 /正規化へ mode 追加。

- Step 2: UI追加
  - モード選択ComboBoxと警告文を追加。
  - `ForceTick` 時に semantic/quiet 設定を無効化。

- Step 3: SceneChangeController 分岐
  - tick内で mode を読み、`ForceTick` なら直接実行分岐へ。
  - `SemanticGate` なら既存処理へ。

- Step 4: ログと可観測性
  - `stage=scene_change event=force_tick_triggered` を追加。
  - `trigger=AutoSceneChangeForcedTick` を run開始ログへ出す。

- Step 5: 回帰確認
  - Semanticモード既存挙動が変わらないこと。
  - ForceTickモードで interval ごと実行されること。

8. **非機能要件チェック**
- 性能:
  - ForceTickは負荷増が前提。実行中重複防止を必須にする。
- 可観測性:
  - モード別ログを必須化し、運用時に判別できるようにする。
- 互換性:
  - defaultは `SemanticGate` で既存互換。
- 運用:
  - UI上で「最終手段」注意を明示。

9. **リスクと緩和策**
- Risk: API課金・GPU/CPU負荷増大。
- Mitigation: デフォルトOFF、明示警告、interval下限を強める。

- Risk: 不要翻訳が増える。
- Mitigation: 運用ガイドに「取りこぼし時のみON」を明記。

- Risk: 利用者が常用し、通常モード改善が進まない。
- Mitigation: ラベルで `Fallback mode` 扱いを明示し、推奨はSemanticに固定。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml`
- `Services/Application/SceneChangeController.cs`
- `Services/Settings/Rules/*`（mode正規化・interval下限）
- `Services/PipelineOrchestrator.cs`（必要なら trigger 種別拡張）

11. **Definition of Done**
- UIで `SemanticGate` / `ForceTick` を切り替え可能。
- `ForceTick` で watch interval ごとに auto-translate run が発火する。
- `SemanticGate` では既存挙動と同等。
- ログでモード別に実行経路を追跡できる。
- default 設定は既存互換（`SemanticGate`）。
