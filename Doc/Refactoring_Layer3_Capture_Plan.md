# Refactoring_Layer3_Capture_Plan

1. **概要（1–3行）**
- 本計画は、層3（Capture）として `CaptureManager` に集中している provider選択・cooldown・black frame判定・固定ウィンドウ解決の責務を分離する実装案である。
- 目的は、DXGI/WGC/GDI の切替ポリシーを明示化し、環境差による不具合時でも挙動を追跡しやすくすること。
- 既存挙動互換（Auto/Fixed provider、black frame耐性、fallback順序）を維持した段階移行で実施する。

2. **ゴール / 非ゴール**
### ゴール
- `CaptureManager` の分岐を「ターゲット解決」「provider選択」「実行ポリシー」「失敗記録」に分割し、責務境界を固定する。
- cooldown / black frame threshold / DXGI timeout 例外扱いのポリシーを `ICapturePolicy` に集約する。
- provider実行結果を共通イベント（success/failure/black/cooldown）として記録し、ログと将来メトリクスに再利用可能にする。

### 非ゴール
- DXGI/WGC/GDI provider 実装のアルゴリズム改善（速度・画質改善）は対象外。
- OCR/Orchestration 層の仕様変更。
- `settings.json` スキーマの大幅変更（必要最小限の互換拡張のみ）。

3. **前提・仮定**
- 層2の分離により、`PipelineOrchestrator` は `CaptureManager.Capture(settings)` を単一入口として利用する前提が成立している。
- 現行 `CaptureManager` は以下責務を同時に持つ。
- provider順序決定（Auto/Fixed）
- 固定キャプチャ対象解決（`WindowBindingService`）
- cooldown 管理
- black frame 判定・閾値処理
- DXGI resident ON/OFF 制御
- これらが単一メソッドに混在し、分岐追加時の回帰点が増えている。

4. **現状整理**
- `CaptureManager`
- 課題: `Capture` で provider順序・state遷移・error理由が密結合。
- 課題: DXGI timeout だけ cooldown除外する特例がローカル分岐で散在しやすい。
- 課題: `_states` は最小構造だが、「なぜ cooldown 入りしたか」の履歴が弱い。

- provider群（`DxgiDuplicationProvider`, `WgcCaptureProvider`, `GdiCaptureProvider`）
- 課題: provider実装は個別最適されているが、運用観点では失敗理由の粒度が統一されていない。
- 課題: fallback結果は分かるが、選択理由（policy判断）を後追いしづらい。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `CaptureTargetResolver`（新規）
- 役割: `CaptureRequest` 構築と固定ウィンドウ解決、解決ログの重複抑制。

- `CaptureProviderSelector`（新規）
- 役割: settings と provider可用性から試行順序を返す（Auto/Fixed + Preferred）。

- `ICapturePolicy` / `DefaultCapturePolicy`（新規）
- 役割: cooldown可否、cooldown期間、DXGI timeout等の特例、black frame閾値判定を集約。

- `CaptureProviderStateStore`（新規）
- 役割: providerごとの `BlackCount/CooldownUntil/LastFailureReason` を管理。

- `CaptureAttemptCoordinator`（新規）
- 役割: 1回の `Capture` 実行における provider試行ループを実行し、成功/失敗/blackを policy に基づき処理。

- `CaptureExecutionTrace`（新規DTO）
- 役割: 「どのproviderをなぜスキップ/失敗/成功したか」を持つ実行記録。

### 5.2 データフロー / シーケンス
1. `PipelineOrchestrator` が `CaptureManager.Capture(settings)` を呼び出す。
2. `CaptureManager` は `CaptureTargetResolver` で `CaptureRequest` を確定。
3. `CaptureProviderSelector` が provider試行順序を決定。
4. `CaptureAttemptCoordinator` が順に試行。
- cooldown中判定（policy）
- `TryCapture` 実行
- black frame 判定（`FrameGate`）
- failure/black時の cooldown 判断（policy）
5. 成功時 `CaptureFrame` を返却、失敗時は `CaptureExecutionTrace` を添えて例外化。

### 5.3 既存パターンへの整合
- `CaptureManager` は facade のまま維持し、外部I/F（`Capture`, `GetCaptureBounds`）は原則不変。
- 既存ログ文言を可能な限り維持しつつ、`stage=capture` と `reason` キーを追加。

6. **インターフェース設計**
### 6.1 主要 DTO / I/F（案）
- `ICapturePolicy`
- `bool ShouldSkipByCooldown(CaptureProviderKind kind, DateTimeOffset now, ProviderRuntimeState state)`
- `bool ShouldStartCooldown(CaptureProviderKind kind, string? error, bool isBlackFrame, AppSettings settings)`
- `DateTimeOffset ResolveCooldownUntil(DateTimeOffset now, AppSettings settings)`

- `ProviderRuntimeState`
- `int BlackCount`
- `DateTimeOffset? CooldownUntil`
- `string? LastFailureReason`

- `CaptureAttemptResult`
- `bool Success`
- `CaptureFrame? Frame`
- `CaptureProviderKind Provider`
- `CaptureFailureReason Reason`（Disabled/Cooldown/CaptureError/BlackThreshold 等）

- `CaptureExecutionTrace`
- `IReadOnlyList<CaptureAttemptResult> Attempts`
- `CaptureProviderKind? SelectedProvider`
- `string Summary`

### 6.2 エラー・バリデーション
- 全provider失敗時は `InvalidOperationException("All capture providers failed.")` を維持し、inner/traceで詳細補強。
- settingsの clamp/normalize は層5責務のため、層3では参照のみを原則とする。

7. **実装手順（ステップ分割）**
- Step 1: 現行挙動の固定
- `CaptureManager` の主要分岐（disabled/cooldown/error/black/success）にログキーを追加し比較基準を固定。

- Step 2: Target 解決の抽出
- `BuildCaptureRequest` + `TrackCaptureTargetResolution` を `CaptureTargetResolver` へ移設。

- Step 3: Provider順序決定の抽出
- `BuildProviderOrder` を `CaptureProviderSelector` へ移設。

- Step 4: Policy 層の導入
- cooldown判定・開始条件・DXGI timeout特例・black閾値判定を `DefaultCapturePolicy` に移設。

- Step 5: State Store 導入
- `_states` 操作（get/reset/start cooldown）を `CaptureProviderStateStore` へ移設。

- Step 6: 試行ループの抽出
- `Capture` の foreach ループを `CaptureAttemptCoordinator` へ移し、`CaptureManager` は orchestration のみ担当。

- Step 7: 旧経路削除と trace 標準化
- `CaptureManager` 内の旧 private helper を削除し、例外/ログに `CaptureExecutionTrace` を付与。

8. **非機能要件チェック**
- 性能
- 1回の capture 実行時間の中央値が現行比 +5% 以内。
- provider切替失敗時の復帰時間（cooldown経由）が現行より悪化しない。

- 可観測性
- providerごとの skip/fail/success がログで機械判定可能。
- DXGI timeout（新規フレームなし）と実エラー（access lost等）を区別して記録。

- 互換性
- `CaptureProviderMode`（Auto/Fixed）と `PreferredCaptureProvider` の意味を維持。
- black frame threshold / cooldown seconds の設定意味を維持。

- 運用
- フェーズ移行中は旧ログキーを残し、新旧比較ができる期間を設ける。

9. **リスクと緩和策**
- Risk: policy抽出で cooldown開始条件が変わり、fallback頻度が増減する。
- Mitigation: 既存条件を先にテーブル化し、移設後に同一入力で比較テストを実施。

- Risk: DXGI timeout特例の扱いミスで "盲目時間" が増える。
- Mitigation: timeout専用のユニットテストを追加し、cooldown非適用を保証。

- Risk: class分割で追跡対象が増える。
- Mitigation: `CaptureManager` を facade として残し、入口を1箇所に固定。

10. **影響範囲**
- 新規候補
- `Services/Capture/CaptureTargetResolver.cs`
- `Services/Capture/CaptureProviderSelector.cs`
- `Services/Capture/ICapturePolicy.cs`
- `Services/Capture/DefaultCapturePolicy.cs`
- `Services/Capture/CaptureProviderStateStore.cs`
- `Services/Capture/CaptureAttemptCoordinator.cs`
- `Services/Capture/CaptureExecutionTrace.cs`

- 既存更新
- `Services/CaptureManager.cs`
- 必要に応じて `Services/DxgiDuplicationProvider.cs`（error理由の標準化のみ）
- 必要に応じて `Models/AppSettings.cs`（将来の微小設定追加時のみ）

- ロールバック手順（案）
- `CaptureManager` 内に旧ループを残した feature flag（例: `UseCaptureCoordinator`）を一時保持し、問題時に即時切戻し可能にする。

11. **Definition of Done**
- [ ] `CaptureManager` の主要分岐が policy/selector/state/coordinator へ分離されている。
- [ ] cooldown/black判定の判断根拠が `ICapturePolicy` で一元化されている。
- [ ] 既存シナリオ（Auto/Fixed、DXGI→WGC→GDI fallback、black frame連続時cooldown）が互換動作する。
- [ ] `dotnet build` / `dotnet run` が成功し、capture関連ログで新旧比較が可能。
- [ ] 旧経路へ戻す手順（feature flagまたは復元手順）が明記されている。
