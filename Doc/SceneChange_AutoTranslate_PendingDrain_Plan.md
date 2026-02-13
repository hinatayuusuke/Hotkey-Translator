# SceneChange 自動翻訳の未発火対策（Pending 1回追いかけ実行）実装案

## 1. 概要（1-3行）
- シーン変化検知時に `OCR実行中` または `クールダウン中` で自動翻訳をスキップした場合、イベントを捨てず「未処理フラグ」に保持する。
- 実行可能になったタイミングで、未処理分を **1回だけ** 追いかけ実行する。
- 連打・連続変化でも実行回数が暴走しないよう、Pending を集約（coalesce）する。

## 2. ゴール / 非ゴール
### ゴール
- タイミング依存で自動翻訳が未発火になる事象を減らす。
- 「スキップされた検知」を 1 回だけ後追い実行できる状態にする。
- 既存の実行制御（`_runInProgress`、クールダウン、既存 `RunOnceAsync`）と整合させる。

### 非ゴール
- シーン変化判定アルゴリズム自体（pHash / threshold）の刷新。
- 複数件キューの厳密順序実行（今回の対象は 1 件集約）。
- 翻訳プロバイダ選択ロジックの変更。

## 3. 前提・仮定
- 現在の問題は「スキップ時にイベント破棄」+「その後のベースライン更新」で再検知しづらくなること。
- `QueueSceneChangeAutoTranslate` は UI スレッドで呼ばれるため、Pending 状態は UI スレッド管理を基本とする。
- 自動翻訳モード時はウォッチャーが継続稼働する。

## 4. 現状整理（問題点）
- シーン変化検知時、以下は即 return で破棄される。
  - クールダウン中
  - OCR 実行中
- 破棄後も比較基準ハッシュは更新されるため、次 Tick で差分が縮み、結果として未発火のまま終わるケースがある。

## 5. 提案アーキテクチャ
### 5.1 状態追加（MainWindow）
- `private bool _sceneChangeAutoTranslatePending;`
- `private int _sceneChangeAutoTranslatePendingDiff;`
- `private int _sceneChangeAutoTranslatePendingThreshold;`
- `private string? _sceneChangeAutoTranslatePendingReason;`
- `private DateTime _lastSceneChangeAutoTranslatePendingLogUtc = DateTime.MinValue;`

### 5.2 新規ヘルパー
- `MarkSceneChangeAutoTranslatePending(int diff, int threshold, string reason)`
  - 未処理フラグを立て、代表値（最大 diff など）を保持。
  - ログは必要最小限（同一 reason は 2 秒以内なら抑制）。

- `TryDrainPendingSceneChangeAutoTranslate()`
  - 条件:
    - `EnableSceneChangeAutoTranslate == true`
    - `pending == true`
    - `_runInProgress == 0`
    - クールダウン経過
  - 成立時:
    - `pending=false` にして `RunOnceAsync(ForceRunOptions.None)` を 1 回実行。
    - 追いかけ実行の開始時刻で `_lastSceneChangeAutoTranslateRequestUtc` を更新する。
    - 戻り値 `true`（drainを起動）を返し、呼び出し側は同Tick処理を打ち切る。

- `ClearSceneChangeAutoTranslatePending(string reason)`
  - モード無効化 / watcher 停止 / 手動明示クリア時に使用。

### 5.3 既存メソッド変更
- `QueueSceneChangeAutoTranslate(int diff, int threshold)`
  - 変更前: スキップ条件で return（破棄）。
  - 変更後: スキップ条件で `Mark...Pending(...)` を呼ぶ。
  - 即時実行できる場合のみ従来どおり実行。

- `RunOnceAsync(ForceRunOptions options)` の finally 末尾
  - `_runInProgress = 0` の後で `TryDrainPendingSceneChangeAutoTranslate()` を呼ぶ。
  - WHY: 実行中理由で積まれた pending を直後に回収できる。

- `OnAutoHideTick(...)` 冒頭
  - auto-translate モード時に `TryDrainPendingSceneChangeAutoTranslate()` を試行。
  - WHY: クールダウン理由の pending を「新規差分が出ない周期」でも回収できる。
  - `TryDrain... == true` の Tick は即 return（同Tickでの再検知処理は行わない）。

- `StopAutoHideWatcher()` / モードOFF遷移
  - `ClearSceneChangeAutoTranslatePending(...)` を呼んで stale pending を破棄。
  - 呼び出し箇所を固定:
    - `StopAutoHideWatcher()`
    - `OnToggleSceneAutoTranslateHotkeyPressed` で `EnableSceneChangeAutoTranslate=false` に遷移した直後
    - `SaveSettingsAsync` 内の `NormalizeSceneChangeModeSettings` 適用後に auto-translate が `false` へ正規化された直後

## 6. データフロー / シーケンス（要点）
1. Tick で diff >= threshold を検知。
2. 即時実行不可（実行中 or cooldown）なら pending に集約。
3. 実行終了時または次 Tick 冒頭で drain 判定。
4. 実行可能になった時点で pending を 1 回実行しクリア。
5. 実行中にさらにイベントが来ても pending は true のまま上書き集約（1回だけ追いかけ）。

## 7. インターフェース設計
- 外部 API 変更なし。
- 設定追加なし（既存 `EnableSceneChangeAutoTranslate`, `SceneChangeWatchIntervalMs` を再利用）。
- ログ追加のみ（`pending set`, `pending drained`, `pending cleared`）。

## 8. 実装手順（ステップ分割）
### Step 1
- Pending 状態フィールドと 3 つのヘルパー実装。

### Step 2
- `QueueSceneChangeAutoTranslate` を pending 対応へ差し替え。

### Step 3
- `RunOnceAsync` finally と `OnAutoHideTick` 冒頭に drain 試行を追加。

### Step 4
- watcher 停止 / モードOFF時の pending クリア導線を追加。

### Step 5
- ログ整備（スキップ理由・drain 実行・クリア理由）。

## 9. 非機能要件チェック
- 性能: フラグ管理のみでオーバーヘッドは軽微。
- 安定性: `_runInProgress` ガードにより同時実行は防止。
- 可観測性: pending lifecycle をログで追跡可能。
- 互換性: 既存設定・既存UIは維持。

## 10. リスクと緩和策
- Risk: drain 試行が多すぎてログノイズ増加。
- Mitigation: 同一 reason の pending set ログを 2 秒窓で間引く。

- Risk: pending 実行が手動 Run と競合。
- Mitigation: `_runInProgress` と既存 gate に完全依存し、競合時は pending 維持のみ。

- Risk: モード切替後に古い pending が誤発火。
- Mitigation: watcher 停止 / auto-translate OFF 時に必ず clear。

## 11. 影響範囲（変更候補）
- `MainWindow.xaml.cs`
  - pending フィールド追加
  - `QueueSceneChangeAutoTranslate` 修正
  - `RunOnceAsync` finally 修正
  - `OnAutoHideTick` / `StopAutoHideWatcher` / モード遷移時の clear 追加

## 12. Definition of Done
- 実行中に検知されたシーン変化が、実行完了後に 1 回だけ追いかけ実行される。
- クールダウン中に検知されたシーン変化が、クールダウン解除後に 1 回だけ実行される。
- auto-translate OFF または watcher 停止後に pending が残留しない。
- 連続変化時でも追いかけ実行は 1 件集約で暴走しない。

## Open Questions
- pending の代表値は「最新 diff」か「最大 diff」か（ログ可観測性の都合）。
