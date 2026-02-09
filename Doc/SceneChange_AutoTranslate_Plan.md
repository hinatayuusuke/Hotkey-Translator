# Scene Change Auto Translate 実装案

1. **概要（1–3行）**
- 既存の `Enable auto-hide on scene change` 監視パイプライン（pHash差分監視）を流用し、シーン変化時に自動で翻訳実行するモードを追加する。
- 初期実装は「検知ロジックは流用」「検知後の動作のみ切替」の最小差分で導入する。
- 既存の手動実行（F8/F10）や Overlay 表示ロジックとの競合を避けるため、連打抑止と排他制御（Auto-hide/Auto-translateは同時ON不可）を明示する。

2. **ゴール / 非ゴール**
- ゴール: シーン変化検知後に `RunOnceAsync(ForceRunOptions.None)` を自動起動できる。
- ゴール: 既存 Auto-hide と同等の監視設定（interval / pHash threshold）を再利用する。
- ゴール: `SceneChangeWatchIntervalMs` を監視間隔かつ再発火抑止間隔として共用する。
- ゴール: 多重起動・高頻度起動を防ぐ最低限のガードを実装する。
- 非ゴール: シーン変化検知アルゴリズム自体（pHash/しきい値）の刷新。
- 非ゴール: OCR/翻訳品質最適化やモデル選択ロジックの変更。

3. **前提・仮定**
- 現状 `MainWindow` には Auto-hide watcher が存在し、`DispatcherTimer` + pHash差分で変化検知している。
- 現状 `RunOnceAsync` には `_runInProgress` ガードがあり、同時実行は抑止される。
- 既存設定の `EnableSceneChangeAutoHide`, `SceneChangeWatchIntervalMs`, `SceneChangeWatchPhashThreshold` はすでに UI/永続化が整っている。
- Auto-translate導入時は watcher 起動条件を `EnableSceneChangeAutoHide || EnableSceneChangeAutoTranslate` に拡張する前提とする。
- Auto-translate は Overlay 表示状態に依存しない仕様とし、Overlay 非表示時も監視を継続する。

4. **現状整理**
- 監視開始/停止: `InitializeAutoHideWatcher` / `UpdateAutoHideWatcher` / `StopAutoHideWatcher`。
- ベースライン更新: `ScheduleAutoHideBaselineReset` で ROI の pHash を基準化。
- 検知処理: `OnAutoHideTick` で差分 `diff >= threshold` 時に Overlay を無効化。
- 問題点: 検知後アクションが Auto-hide に固定されており、Auto-translate へ再利用するための分岐ポイントが明示されていない。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `AppSettings` に自動翻訳モード設定を追加（案: `EnableSceneChangeAutoTranslate`）。
  - `MainWindow` に UIトグルと watcher動作分岐（hide / translate）を追加し、両トグルを排他制御する。
  - Auto-hide watcher 自体（capture + ROI + pHash）は既存実装を流用。
- データフロー / シーケンス:
  1. タイマー tick で現フレーム ROI を取得し pHash 差分を計算。
  2. `diff >= threshold` 検知時、設定に応じて動作を選択。
  3. Auto-hide 有効: 従来どおり Overlay 非表示。
  4. Auto-translate 有効: Overlay 表示状態に関係なく、検知ごとに UI スレッドへ起動要求をキュー投入し、実行中ならログして破棄する。
  5. 成功/失敗に関わらず、監視基準は既存方針で更新する（過剰再発火を抑制）。
- 既存パターンへの整合:
  - 監視処理は `OnAutoHideTick` を拡張し、検知ロジックの複製を避ける。
  - 自動翻訳の起動は手動実行と同じ `RunOnceAsync` 経路を通し、挙動差分を最小化する。

6. **インターフェース設計**
- `AppSettings` 追加項目（案）:
  - `EnableSceneChangeAutoTranslate: bool = false`
- UI（OCR設定）:
  - `Enable auto-translate on scene change` チェックを追加。
  - `Enable auto-hide on scene change` と排他（どちらか一方のみON可）。
- 実行判定ルール（初期案）:
  - `triggered = diff >= threshold`
  - `triggered && EnableSceneChangeAutoTranslate == true` のとき翻訳起動。
  - 直近起動から `SceneChangeWatchIntervalMs` 未満ならスキップ（連打防止）。
  - Auto-translate 有効時は `overlayVisible` 条件で監視を停止しない（Overlay 非表示でも tick 継続）。
  - 排他ルール: `EnableSceneChangeAutoTranslate=true` にした場合は `EnableSceneChangeAutoHide=false` へ自動補正（逆も同様）。
  - 旧設定補正ルール: 起動時に両方 `true` の場合は `EnableSceneChangeAutoHide=true` を優先し、`EnableSceneChangeAutoTranslate=false` へ補正する。

7. **実装手順（ステップ分割）**
- Step 1: 設定拡張
  - `Models/AppSettings.cs` に `EnableSceneChangeAutoTranslate` を追加。
  - 既存設定ロード/保存フローへ自然統合（追加マイグレーション不要）。
- Step 2: UI追加
  - `MainWindow.xaml` の OCR/Scene Change セクションにトグルを追加し、既存 Auto-hide と排他運用であることを明記。
  - `MainWindow.xaml.cs` の `ApplySettingsToUi` / `SaveSettingsAsync` で排他補正を実装し、起動時の旧設定両ONは Auto-hide 優先で正規化する。
- Step 3: watcher分岐追加
  - `UpdateAutoHideWatcher` / `ScheduleAutoHideBaselineReset` の起動条件を `Auto-hide OR Auto-translate` に拡張し、Auto-translate 時は `overlayVisible` に依存させない。
  - `OnAutoHideTick` の `diff >= threshold` 分岐で action を切替。
  - Auto-translate 分岐で UI スレッドへ `RunOnceAsync(ForceRunOptions.None)` 起動要求をキュー投入。
  - 起動要求処理時に `_runInProgress` の場合は実行せず、スキップログを記録。
  - `_runInProgress` に加え、`SceneChangeWatchIntervalMs` を使った再発火ガード（時刻）を追加。
- Step 4: ログ・可観測性
  - `Scene change detected: auto-translate triggered (diff=..., threshold=...)` を追加。
  - スキップ理由（cooldown/実行中）をログに明示。

8. **非機能要件チェック**
- 性能: 監視処理は既存流用。追加コストは検知時の翻訳起動のみ。
- セキュリティ: 新規外部I/Fなし。既存翻訳プロバイダの認証/通信経路を利用。
- 可観測性: 検知ログ・起動ログ・スキップログを追加し、挙動追跡を容易化。
- 互換性: 既定値 `false` で既存挙動を不変化。
- 運用: 閾値/監視間隔は既存パラメータを再利用し、運用設定数を増やしすぎない。

9. **リスクと緩和策**
- Risk: シーン変化が多い画面で翻訳起動が頻発し、体感負荷が上がる。
- Mitigation: `SceneChangeWatchIntervalMs` 共用クールダウン + 実行中スキップ + しきい値調整で発火頻度を抑制。
- Risk: Auto-hide と Auto-translate の同時有効時に期待挙動が曖昧。
- Mitigation: 仕様を排他（同時ON不可）に固定し、保存時/起動時に自動補正する（旧設定両ONは Auto-hide 優先）。
- Risk: watcher起点の非同期処理でログが追いづらくなる。
- Mitigation: tickごとの決定結果（trigger/skip/execute）を簡潔に記録。
- Risk: Overlay 非表示中も監視することで、想定外の自動翻訳起動が起きる可能性。
- Mitigation: UI説明とログに「Auto-translate は Overlay 表示状態に依存しない」ことを明記する。

10. **影響範囲**
- `Models/AppSettings.cs` — Auto-translateトグル設定追加。
- `MainWindow.xaml` — Scene Change設定にトグル追加。
- `MainWindow.xaml.cs` — UI反映/保存、排他補正（旧設定両ONはAuto-hide優先）、watcher起動条件拡張、UIスレッドキュー投入分岐、`SceneChangeWatchIntervalMs` 共用ガード、ログ追加。
- （必要に応じて）`Doc/` — 最終仕様を反映した運用メモ追記。

11. **Definition of Done（完了条件チェックリスト）**
- [ ] `Enable auto-translate on scene change` ON で、しきい値超過時に自動翻訳が実行される。
- [ ] OFF 時は従来どおり自動翻訳は起動しない。
- [ ] Auto-hide と Auto-translate が同時ONにならない（保存時/起動時に排他補正され、旧設定両ONはAuto-hide優先になる）。
- [ ] Auto-translate の起動要求は UI スレッドに投入され、実行中はログ付きで破棄される。
- [ ] Auto-translate 有効時は Overlay 非表示でも監視と自動翻訳判定が継続される。
- [ ] 既存 Auto-hide 機能が回帰しない。
- [ ] 連続シーン変化時に多重起動/過剰発火が抑制される。
- [ ] ログで「検知」「起動」「スキップ理由」が判別できる。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
