# Refactoring_Layers_1to5_Master_Plan

1. **概要（1–3行）**
- 本ドキュメントは、リポジトリ全体のリファクタリング優先度（1～5層）を統一し、後続の個別実装案の親方針とする。
- 目的は「機能追加を止めずに保守性を上げる」ことであり、段階的に責務分離を進める。
- 実装は既存挙動互換を前提とし、機能仕様変更は別Docで扱う。

2. **ゴール / 非ゴール**
### ゴール
- 変更優先度を 1) Application 2) Orchestration 3) Capture 4) gRPC Host 5) Settings に固定する。
- 各層で「責務境界」「移行順」「完了条件（DoD）」を定義する。
- 後続の個別実装案が、同じ品質基準（互換性・観測性・ロールバック性）で作成できる状態にする。

### 非ゴール
- 一括大改修（Big Bang）で全層を同時に作り替えること。
- OCR精度や翻訳品質そのものの改善（それは個別機能Docで扱う）。
- UI仕様変更をこのDoc単体で確定すること。

3. **前提・仮定**
- 現在の主な課題は `MainWindow.xaml.cs` への責務集中と、実行フローの分岐増大。
- Scene-change/auto-translate 等の新機能追加により、状態管理の複雑さが上がっている。
- 既存ユーザー設定（`settings.json`）との互換維持が重要。

4. **現状整理（層別の問題）**
- 層1: Application（`MainWindow.xaml.cs`）
- 問題: UIイベント、実行制御、watcher状態、設定正規化が混在し肥大化。
- 層2: Orchestration（`PipelineOrchestrator` / `SceneTextSnapshotService`）
- 問題: 正常系とフォールバック系の分岐が増え、拡張時の回帰リスクが高い。
- 層3: Capture（`CaptureManager` / DXGI/WGC/GDI）
- 問題: provider切替・cooldown・black frame判定のポリシーが複雑。
- 層4: Host（Llama/Paddle/PaddleVL/CT2 gRPC host）
- 問題: 起動/監視/再起動の重複実装が多く、横展開漏れが起きやすい。
- 層5: Settings（`AppSettings` / `SettingsService`）
- 問題: 設定項目増加で検証・正規化責務が散在。

5. **提案アーキテクチャ（全体方針）**
### 5.1 優先順位（固定）
1. Application層分離（最優先）
2. Orchestration層分離
3. Capture層抽象化
4. gRPC Host基盤共通化
5. Settings層モジュール化

### 5.2 横断原則
- 原則A: 1フェーズ1責務。機能追加と構造変更を同時にやらない。
- 原則B: 挙動互換を優先。外部I/F変更はアダプタで吸収。
- 原則C: 旧経路は段階的に残し、観測ログで新経路との差分を確認してから削除。
- 原則D: すべてのフェーズでロールバック手順を明文化する。

### 5.3 フェーズ設計
- Phase 0（準備）
- 目的: 計測・ログ・回帰確認導線を先に整える。
- 産物: 主要フローのログキー固定、最低限のスモークテスト手順。

- Phase 1（層1: Application）
- 目的: `MainWindow` の責務を「UI入力/表示」に寄せる。
- 方向: `SceneChangeController` / `RunCoordinator` / `SettingsNormalizer` を導入。

- Phase 2（層2: Orchestration）
- 目的: OCR→差分→翻訳→Overlay の段階責務を明確化。
- 方向: Step実行単位の小サービス化、payload再利用判定の独立。

- Phase 3（層3: Capture）
- 目的: providerポリシーとデバイス依存処理を分離。
- 方向: cooldown/retry/black判定を共通ポリシー化。

- Phase 4（層4: gRPC Host）
- 目的: ホスト管理の重複削減。
- 方向: 共通基底（例: `GrpcHostBase`）とエンジン固有設定の分離。

- Phase 5（層5: Settings）
- 目的: 設定の可読性・検証可能性・互換処理の集中化。
- 方向: 機能単位サブ設定（Scene/OCR/Overlay/Translation）+ validation集約。

6. **インターフェース設計（高レベル）**
- 層1
- `IMainWindowController`（UIイベント受信）
- `ISceneChangeController`（watcher状態・発火判断）

- 層2
- `IOcrPipelineStage`（Capture/OCR/Group/Diff/Translate/Overlay）
- `ISceneSemanticGate`（Stage A/B評価とpayload管理）

- 層3
- `ICapturePolicy`（cooldown/black frame/retry）
- `ICaptureProviderSelector`（固定/自動選択）

- 層4
- `IGrpcHostLifecycle`（Start/Stop/Restart/Health）
- `GrpcHostOptions`（共通起動パラメータ）

- 層5
- `AppSettingsValidator`（normalize/clamp/compat）
- `FeatureSettings`（機能単位の設定構造体）

7. **実装手順（段階移行）**
- Step 1: Phase 0（計測点と検証手順を固定）
- Step 2: Phase 1（`MainWindow` からScene-change/Run制御を切り出し）
- Step 3: Phase 2（`PipelineOrchestrator` の段階分割）
- Step 4: Phase 3（Captureポリシー層導入）
- Step 5: Phase 4（gRPC host共通基盤化）
- Step 6: Phase 5（Settingsモジュール化）
- Step 7: 旧コード削除と最終整備（ログ・Doc・移行注意点）

8. **非機能要件チェック**
- 性能: フェーズごとに既存ベースラインを下回らないこと（OCR実行時間・watcher tick）。
- 可観測性: 旧経路/新経路の判別ログを必須化。
- 互換性: `settings.json` 既存値を壊さない（読み込み時互換維持）。
- 運用性: 問題発生時にフェーズ単位で戻せること。

9. **リスクと緩和策**
- Risk: 分割途中で責務境界が曖昧になり、逆に複雑化する。
- Mitigation: 各Phaseで「移管する責務一覧」を先に固定し、範囲外変更を禁止。

- Risk: 回帰（発火条件や翻訳タイミングのズレ）が発生する。
- Mitigation: 既存ログキーと比較可能な検証シナリオを維持する。

- Risk: 共通化の過剰設計で進捗が止まる。
- Mitigation: まず重複の高い箇所だけ抽出し、抽象化は最小限から開始。

10. **影響範囲（高レベル）**
- `MainWindow.xaml.cs`
- `Services/PipelineOrchestrator.cs`
- `Services/CaptureManager.cs` + provider群
- `Services/*GrpcHost.cs` 群
- `Models/AppSettings.cs`, `Services/SettingsService.cs`
- 関連Doc（各Phaseの個別Plan）

11. **Definition of Done（全体）**
- [ ] 1～5層すべてで責務境界が文書化されている。
- [ ] 各層に個別実装Planが作成され、親方針との対応が明記されている。
- [ ] 既存主要ユースケース（手動Run / Scene auto-translate / Auto-hide / 設定保存）が互換で動作する。
- [ ] ロールバック手順がPhase単位で定義されている。
- [ ] 旧経路削除前に、ログベースで新旧挙動差分が許容範囲内であることを確認している。
