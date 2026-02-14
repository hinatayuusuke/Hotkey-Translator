# Refactoring_Layer4_GrpcHost_Plan

1. **概要（1–3行）**
- 本計画は、層4（gRPC Host）として `Paddle/PaddleVL/CTranslate2/Llama` の各 Host に重複している起動・監視・再起動・ヘルスチェック責務を共通基盤へ統合する実装案である。
- 目的は、Host 追加/変更時の横展開漏れを防ぎ、障害時の挙動（retry/restart/disable）の一貫性を高めること。
- 既存挙動互換を前提に、`ResourceHostFacade` を入口として段階移行する。

2. **ゴール / 非ゴール**
### ゴール
- `IGrpcHostLifecycle` / `GrpcHostBase` を導入し、`StartAsync/Stop/MonitorLoop/RestartWindow/ReadyProbe` の共通処理を再利用化する。
- Host 固有差分（起動引数・事前準備・ヘルスクライアント型・停止時後処理）だけを派生側へ分離する。
- `ResourceHostFacade` の Host 管理を registry ベースへ寄せ、起動失敗時の disable 振る舞いを統一する。

### 非ゴール
- OCR/翻訳エンジン仕様の変更。
- Llama モデル取得戦略や CUDA 依存管理ロジックの最適化（既存ロジックを移設するのみ）。
- Settings スキーマの大改修（層5で実施）。

3. **前提・仮定**
- 現行には `PaddleGrpcHost`, `PaddleVlGrpcHost`, `CTranslate2GrpcHost`, `LlamaGrpcHost` があり、次の責務が重複している。
- start guard（`_process` 稼働確認）
- gRPC unencrypted switch
- process start + stdout/stderr log
- ready loop + timeout
- monitor loop + restart window
- stop/dispose
- `ResourceHostFacade` は Host ごとに `TryStartXxx` を持ち、失敗時 disable 方針が重複している。
- `ShouldLoadCTranslate2` は現在 `false` 固定で退役パスだが、構造上は Host 管理対象として残っている。

4. **現状整理**
- `Services/*GrpcHost.cs`
- 課題: 同型コードが多く、修正漏れリスクが高い。
- 課題: ログキーが Host ごとに不統一で、運用時比較が難しい。
- 課題: restart policy（window/max）の実装が分散し、変更時の検証工数が増える。

- `Services/Application/ResourceHostFacade.cs`
- 課題: Host ごとの if/else 分岐と error handling が肥大化。
- 課題: 新 Host 追加時に `ShouldLoad/TryStart/Disable/Stop` を複数箇所更新する必要がある。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `IGrpcHostLifecycle`（新規）
- 役割: `Task StartAsync(AppSettings, CancellationToken)`, `void Stop()`, `bool IsRunning` の共通契約。

- `GrpcHostBase`（新規 abstract）
- 役割: 起動/監視/再起動/停止の共通テンプレート。
- 派生側が実装する責務:
- `BuildStartInfo(AppSettings)`
- `ProbeReadyAsync(AppSettings, CancellationToken)`
- `GetRestartPolicy(AppSettings)`
- `OnBeforeStartAsync(AppSettings, CancellationToken)`（任意）
- `OnAfterStop()`（任意）

- `GrpcHostRestartPolicy`（新規 DTO）
- 役割: `MaxRestarts`, `Window`, `BackoffMs` を統一表現。

- `GrpcHostDescriptor`（新規）
- 役割: Host 種別、Enable 判定、Stop conflict、Start message、Failure disable action を定義。

- `GrpcHostRegistry` / `GrpcHostOrchestrator`（新規）
- 役割: `ResourceHostFacade` から分離して Host 群を宣言的に起動/停止する。

### 5.2 データフロー / シーケンス
1. `ResourceHostFacade` が settings を渡して `GrpcHostOrchestrator.EnsureHostsAsync` を呼ぶ。
2. orchestrator が `GrpcHostDescriptor` で対象 Host と競合停止順を決定。
3. 各 Host は `GrpcHostBase.StartAsync` を通じて起動。
4. ready probe 成功後に monitor loop 開始、異常終了時は共通 restart policy で再起動。
5. 失敗時は descriptor 経由で settings disable/action を適用し UI へ通知。

### 5.3 既存パターンへの整合
- 既存 public API（`PaddleGrpcHost` 等の `StartAsync/Stop/IsRunning`）は維持しつつ内部を base 委譲に置換。
- `ResourceHostFacade` は外部契約を保持し、内部で registry/orchestrator を利用。
- CTranslate2 退役方針（`ShouldLoadCTranslate2=false`）は維持。

6. **インターフェース設計**
### 6.1 主要 DTO / I/F（案）
- `IGrpcHostLifecycle`
- `bool IsRunning { get; }`
- `Task StartAsync(AppSettings settings, CancellationToken cancellationToken)`
- `void Stop()`

- `GrpcHostBase`
- 共通: lock, process, monitor task, restart history, stop flag, output/error log hook
- 抽象: start info 構築、ready probe、restart policy

- `IGrpcHealthProbe`（任意）
- `Task<bool> IsReadyAsync(string endpoint, CancellationToken token)`
- 実装例: `OcrGrpcHealthProbe`, `TranslationGrpcHealthProbe`

- `GrpcHostDescriptor`
- `string HostId`
- `Func<AppSettings, bool> ShouldLoad`
- `IReadOnlyList<string> StopBeforeStartHostIds`
- `Func<AppSettings, string> BusyMessage`
- `Action<AppSettings> DisableOnFailure`

### 6.2 エラー・バリデーション
- Host 起動失敗時は現行同様にログ + host stop + settings disable + UI failure message。
- settings normalize は層5責務のため、層4では既存 normalize 呼び出し位置を維持する。

7. **実装手順（ステップ分割）**
- Step 1: 現行ログキー固定
- `stage=grpc_host host=<id> event=start/ready/restart/stop/fail` を既存実装へ先行追加。

- Step 2: 共通契約導入
- `IGrpcHostLifecycle`, `GrpcHostRestartPolicy` を追加し、既存 Host を型として適合。

- Step 3: `GrpcHostBase` 導入
- Start/Stop/Monitor/CanRestart/WaitReady の共通骨格を実装。

- Step 4: Host 移行（Paddle/PaddleVL/CT2）
- 3 Host を base 継承に置換し、差分は start args と endpoint/probe のみに限定。

- Step 5: Llama 移行
- `OnBeforeStartAsync` に uv sync / model validation / CUDA validation を移設。
- `OnAfterStop` で orphan llama-server kill fallback を維持。

- Step 6: Resource 側の共通化
- `ResourceHostFacade` から起動分岐を縮小し、`GrpcHostOrchestrator` + `GrpcHostDescriptor` ベースへ置換。

- Step 7: 旧経路削除
- Host ごとの重複 private helper を削除し、共通基盤へ一本化。
- 旧ログ文言は運用に必要なものを残し、新キーへ統合。

8. **非機能要件チェック**
- 性能
- 起動所要時間（host readyまで）を現行比 +5% 以内。
- monitor loop のポーリング負荷を悪化させない。

- 可観測性
- すべての Host で統一ログキー（host id/event/reason/restartCount）を出力。

- 互換性
- 起動・停止ホットキーや ResourceHostFacade からの操作互換を維持。
- 起動失敗時の設定自動OFF挙動を維持。

- 運用
- 段階移行中は feature flag（例: `UseUnifiedGrpcHostRuntime`）で新旧切替可能にする。

9. **リスクと緩和策**
- Risk: 共通基盤化で Llama 固有の複雑要件（uv/model/CUDA/orphan kill）が欠落する。
- Mitigation: `GrpcHostBase` に拡張フック（BeforeStart/AfterStop）を設け、Llama は専用処理を保持。

- Risk: restart policy 統合で再起動頻度が変わる。
- Mitigation: 現行 settings 項目（RestartMax/WindowSeconds）をそのまま `GrpcHostRestartPolicy` にマッピング。

- Risk: ResourceHostFacade 置換時に競合停止順（Paddle vs PaddleVL、CT2 vs Llama）が崩れる。
- Mitigation: `StopBeforeStartHostIds` を descriptor で明示し、順序テストを追加。

10. **影響範囲**
- 新規候補
- `Services/GrpcHost/IGrpcHostLifecycle.cs`
- `Services/GrpcHost/GrpcHostBase.cs`
- `Services/GrpcHost/GrpcHostRestartPolicy.cs`
- `Services/GrpcHost/GrpcHostDescriptor.cs`
- `Services/GrpcHost/GrpcHostRegistry.cs`
- `Services/GrpcHost/GrpcHostOrchestrator.cs`
- `Services/GrpcHost/HealthProbe/*`

- 既存更新
- `Services/PaddleGrpcHost.cs`
- `Services/PaddleVlGrpcHost.cs`
- `Services/CTranslate2GrpcHost.cs`
- `Services/LlamaGrpcHost.cs`
- `Services/Application/ResourceHostFacade.cs`

- ロールバック手順（案）
- `ResourceHostFacade` に新旧 runtime 切替フラグを一時保持し、障害時は旧 Host 実装経路へ即時切戻し。

11. **Definition of Done**
- [ ] 4 Host の共通処理（start/ready/monitor/restart/stop）が `GrpcHostBase` に集約されている。
- [ ] Host 固有差分が最小責務（起動引数・preflight・停止後処理）へ整理されている。
- [ ] `ResourceHostFacade` の Host 起動分岐が registry/orchestrator 経由に置換されている。
- [ ] 既存主要シナリオ（Paddle/PaddleVL 起動、Llama起動、失敗時自動OFF、手動停止）が互換動作する。
- [ ] `dotnet build` / `dotnet run` が成功し、統一ログキーで挙動追跡できる。
- [ ] ロールバック手順（新旧切替）が明記されている。
