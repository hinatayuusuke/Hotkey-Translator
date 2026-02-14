# Scene Change Auto-Translate Quiet Window 実装案

1. **概要（1–3行）**
- ノベルゲーム向けに、Stage B（セマンティック変化）検知後の即時実行をやめ、一定時間の無変化を待ってから自動翻訳を実行する。
- 文字送り中は待機タイマーを延長し続け、テキスト停止時点のスナップショットを採用する。
- 既存の pending 機構を活用しつつ、pending payload は常に最新版で上書きする。

2. **ゴール / 非ゴール**
### ゴール
- 文字送り途中の中途半端なOCR結果で自動翻訳が走る頻度を下げる。
- ノベルゲームで「会話確定後のまとまった文」を取りやすくする。
- UIから Quiet Window の ON/OFF と待機時間(ms)を操作可能にする。

### 非ゴール
- OCRエンジン本体（WinRT/Paddle/Paddle-VL）の認識アルゴリズム変更。
- シーン変化 Stage A（pHash）計算方式の変更。
- 手動実行（F8/F10）の挙動変更。

3. **前提・仮定**
- 現状は Stage A 通過後、Stage B 通過時に即 `QueueSceneChangeAutoTranslate(...)` へ進む。
- 既存に pending 管理（cooldown/running中の保留）があり、drain で再実行する構造がある。
- ノベルゲームでは「同一吹き出し内でテキスト増加」が連続発生し、即時実行だと途中文になりやすい。
- 既存仕様どおり `EnableSceneChangeAutoHide` と `EnableSceneChangeAutoTranslate` の排他は維持する（同時有効化は扱わない）。

4. **現状整理**
- Stage B 通過直後に自動翻訳を起動するため、文字送り途中で実行される。
- `SceneSemanticRequireConfirmTicks` だけでは「連続検知」対策はできても「停止待ち」にはならない。
- pending payload は条件次第で保持されるが、実行直前の最終スナップショットに寄せる保証が弱い。
- Stage A（pHash）が閾値未満で通らないケースには Stage B 改善だけでは効かない。

5. **提案アーキテクチャ**
### 5.1 Quiet Window（無変化待機）
- 新規概念: `Quiet Window`。
- Stage B で semantic change を検知しても即実行せず、`最後の変化検知時刻` を更新して待機に入る。
- 待機中に再度 semantic change が来たら時刻を更新（タイマー延長）。
- `現在時刻 - 最後の変化検知時刻 >= QuietWindowMs` で自動翻訳実行を許可。

### 5.2 pending payload 最新化
- 保留状態では `pending payload` を常に最新 snapshot で上書きする。
- 実行時はこの最新 payload のみ使用し、途中経過の古い payload は破棄する。
- これにより「文字送り末尾に近い内容」を優先できる。

### 5.3 UI ON/OFF
- Quiet Window は UI で有効/無効を切替可能にする。
- 有効時のみ `QuietWindowMs` 入力欄を有効化する。

### 5.4 Quiet経路のTTLポリシー
- 推奨: quiet経路は payload TTL 判定を通常経路と別扱いにする。
- `RunTrigger.AutoSceneChange` かつ `EnableSceneChangeQuietWindow=true` の payload は TTL 失効より `SnapshotSignature` 一致を優先して採用する。
- これにより Quiet Window 待機時間と既存TTL（500ms）の衝突で payload が無駄に破棄される問題を回避する。

6. **インターフェース設計**
### 6.1 AppSettings 追加
- `EnableSceneChangeQuietWindow: bool`（default: `true`）
- `SceneChangeQuietWindowMs: int`（default: `450`）

### 6.2 バリデーション
- `SceneChangeQuietWindowMs` は `100..3000` に clamp。
- `EnableSceneChangeQuietWindow=false` のときは現行どおり即時実行。
- `SceneChangeQuietWindowMs` の UI入力は以下ルールを採用する:
  - blank: 既定値 `450` を採用
  - parse失敗: 変更前値を維持
  - 範囲外: clamp して保存

### 6.3 UI項目（OCR > Scene Change Automation）
- `Enable quiet window for auto-translate`（CheckBox）
- `Quiet window (ms)`（TextBox、整数）
- Tooltip/説明文例:
  - `Wait until text stops changing before auto-translate.`

### 6.4 Controller責務
- `SceneChangeController` に以下の状態を追加:
  - `_quietWindowPending: bool`
  - `_quietWindowLastChangeUtc: DateTime`
  - `_quietWindowPendingDiff: int`
  - `_quietWindowPendingThreshold: int`
- pending 更新は単一経路（例: `UpdatePendingAutoTranslate(...)`）に集約し、保留理由に関係なく payload を常に最新版へ更新。

### 6.5 Drain統制
- `TryDrainPendingAutoTranslate()` 内で Quiet Window 条件を必ず評価し、未達なら drain を拒否する。
- これにより `Run finally` 側の drain 呼び出し経路でも quiet未達の即時実行を防ぐ。

7. **実装手順（ステップ分割）**
- Step 1: Settings/VM/UI 追加
  - `AppSettings`・`SettingsViewModel`・`MainWindow.xaml` に Quiet Window 設定を追加。
  - 保存/読込/正規化ルールを実装。

- Step 2: SceneChangeController に Quiet Window 状態追加
  - Stage B 通過時に即実行せず、Quiet Window pending を開始/延長する分岐を追加。
  - Quiet Window 無効時は従来挙動（即実行）を維持。

- Step 3: Tick時の実行条件変更
  - `TryDrainPendingAutoTranslate()` 自身に Quiet Window 条件を内包して評価。
  - quiet未達なら実行せず、pending維持。
  - quiet達成時にのみ `_runOnceAsync(AutoSceneChangeRunOptions, latestPayload)`。

- Step 4: pending payload 最新化の一貫化
  - 保留理由（cooldown/running/quiet）に関係なく、snapshot が来たら payload を上書き。
  - ログに `pending_reason` と `payload_age_ms` を出して追跡可能にする。

- Step 5: 回帰確認
  - 手動実行・通常自動翻訳の既存挙動を確認。
  - `auto-hide / auto-translate` の排他仕様が維持されることを確認。

8. **非機能要件チェック**
- 性能:
  - OCR回数自体は大きく増やさない（既存監視tickを流用）。
  - 追加負荷は時刻比較と状態更新中心で軽微。
- 可観測性:
  - `stage=scene_change event=quiet_pending` / `quiet_extended` / `quiet_ready` を追加。
- 互換性:
  - Quiet Window OFF で従来互換。
- 運用:
  - 既定ONにしてノベル向け体験を改善、必要ならUIでOFF可能。
  - Stage A が通らないケースは別途しきい値調整で対応（推奨: `SceneChangeWatchPhashThreshold=1..3`）。

9. **リスクと緩和策**
- Risk: 待機を長くしすぎると翻訳が遅く感じる。
- Mitigation: UIでms調整可能にし、既定値を中庸（450ms）に設定。

- Risk: payload上書きで極端な揺れ時に実行タイミングが遅延する。
- Mitigation: quiet上限（3000ms）と監視間隔の下限を維持し、ログで追跡。

- Risk: Quiet Window 待機と既存 payload TTL が競合し、payload 再利用率が低下する。
- Mitigation: quiet経路のTTLを別扱いにし、`SnapshotSignature` 一致を採用条件の主軸にする。

- Risk: drain 経路の実行で quiet未達を迂回する可能性。
- Mitigation: `TryDrainPendingAutoTranslate()` 内に quiet判定を実装し、単一路でガードする。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- `Models/AppSettings.cs`
  - Quiet Window 設定2項目追加。
- `ViewModels/SettingsViewModel.cs`
  - 設定項目の LoadFrom/ApplyTo/Changed Hook 追加。
- `MainWindow.xaml`
  - Scene Change Automation セクションに UI 項目追加。
- `Services/Application/SceneChangeController.cs`
  - Quiet Window 状態管理・drain条件・pending payload最新化。
- `Services/Settings/Rules/*`（必要に応じて）
  - `SceneChangeQuietWindowMs` clamp ルール追加。
- `Doc/`
  - 運用ガイド（推奨値: ノベル向け）追記。

11. **Definition of Done（完了条件）**
- Quiet Window ON で、文字送り中は自動翻訳が発火せず、停止後に発火する。
- Quiet Window OFF で、現行と同等の即時発火挙動になる。
- pending payload が常に最新で上書きされ、実行時に最新内容が使われる。
- UIから ON/OFF と ms の変更が可能で、settings.json へ保存/復元される。
- ログで quiet待機・延長・実行トリガーが追跡できる。
- `auto-hide / auto-translate` の排他仕様が現行どおり維持される。
