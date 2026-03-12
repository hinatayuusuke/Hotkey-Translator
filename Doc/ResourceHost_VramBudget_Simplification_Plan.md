# Resource Host VRAM Budget Simplification Plan

1. 概要
- 現行の OCR / 翻訳 host の排他ロジックは、用途ごとの例外 (`Paddle/NDL` 共存、`VisionLLM` hybrid helper、`Vision shared translation` での `Llama` 停止など) が増え、判断経路が分散している。
- 本案では、主 OCR は 1 つに固定したまま、補助 OCR / 翻訳 host の常駐可否を `VRAM budget` ベースで一元管理する。

2. ゴール / 非ゴール
- ゴール:
  - host 排他ロジックを `VRAM budget` と `主 OCR 単一` の 2 軸へ簡素化する。
  - `ShouldLoad*`, `ShouldKeep*Resident`, `StopBeforeStartHostIds` に分散した判断を 1 箇所へ集約する。
  - Vision shared translation のような特例停止を budget 判定へ吸収する。
  - 主 OCR + 翻訳の必須組み合わせだけで budget 超過する場合は、設定変更を fail fast で拒否し、前回設定へ戻す。
- 非ゴール:
  - OCR / 翻訳アルゴリズム自体の変更。
  - `WinRT` のような non-host engine を無理に VRAM 管理へ乗せること。
  - 実際の VRAM 使用量のリアルタイム計測。

3. 前提・仮定
- `OcrEngine` は今後も単一選択であり、主 OCR は常に 1 つである。
- host の重さは実測 VRAM ではなく、まずは静的な budget weight で扱う。
- `WinRT` は host 常駐を持たないため、budget 対象外とする。
- `NDL` は CPU 実行前提のため budget 対象外とする。
- `PaddleOCR-VL` と `VisionLLM` は重いだけでなく主 OCR として競合しやすいため、主 OCR 単一ルールは残す。
- Hybrid helper OCR も、GPU 実行なら budget に含める。
- VisionLLM が自前翻訳を使う経路では、純粋な `Llama` translation host は budget required に含めない。

4. 現状整理
- `Services/Application/ResourceHostFacade.cs`
  - `ShouldLoadPaddle`, `ShouldLoadPaddleVl`, `ShouldLoadNdl`, `ShouldLoadVisionLlm`, `ShouldLoadLlama`
  - `ShouldKeepPaddleResident`, `ShouldKeepNdlResident`, `ShouldKeepVisionLlmResident`
  - `UseVisionSharedLocalTranslation`, `ShouldStopLlamaAsUnused`
  - `StopBeforeStartHostIds`
- 問題点:
  - 起動判定、常駐判定、依存停止が別の関数・別の仕組みに分かれている。
  - `VisionLLM + helper OCR + local translation` のような複合ケースで理解コストが高い。
  - 将来エンジンが増えると分岐爆発しやすい。
  - `EnableLlamaCppTranslation=true` だけでは、実際にその run で `Llama` host が必要かどうかを表せない。

5. 提案アーキテクチャ
- コンポーネント構成:
  - `ResourceHostFacade` に新しい host planning レイヤを追加する。
  - 既存 `GrpcHostOrchestrator` は「計画された host を start/stop する実行器」に寄せる。
- データフロー:
  1. `BuildDesiredHostSet(settings)`
     - 主 OCR / 補助 OCR / 翻訳設定から「欲しい host」を集める
  2. `ApplyVramBudget(settings, desiredHosts)`
     - host ごとの weight を使い、予算超過時に optional host を落とす
  3. `ValidateRequiredBudget(settings, plannedHosts)`
     - 主 OCR + 翻訳など required host だけで超過していれば UI にエラーを返し、設定変更を拒否する
  4. `ReconcileHosts(plannedHosts)`
     - 起動中 host と比較し、stop/start を実行する
- 既存パターンへの整合:
  - 主 OCR は 1 つだけ選ばれるので、その host は必須扱い
  - helper OCR と local translation は budget の中で optional host として扱う
  - `EnableLlamaCppTranslation` は単独では required 判定に入れず、`UsesVisionLocalTranslation(settings)` を見て「実際に使う翻訳経路」を決める

6. インターフェース設計
- 新規 enum / model 案:
  - `HostPlanRole` (`PrimaryOcr`, `HelperOcr`, `Translation`)
  - `PlannedHost(HostId, Role, BudgetWeight, Required, Reason)`
  - `GraphicsResourceBudgetProfile` (`LowVram`, `Balanced`, `HighVram`, `UltraVram`)
- 新規 helper:
  - `BuildDesiredHostSet(AppSettings settings)`
  - `ApplyVramBudget(AppSettings settings, IReadOnlyList<PlannedHost> desired)`
  - `ValidateRequiredBudget(AppSettings settings, IReadOnlyList<PlannedHost> planned)`
  - `GetHostBudgetWeight(AppSettings settings, string hostId)`
  - `GetBudgetLimit(AppSettings settings)`
  - `UsesVisionLocalTranslation(AppSettings settings)`
- `UsesVisionLocalTranslation(AppSettings settings)` の初期案:
  - `settings.OcrEngine == OcrEngineKind.VisionLlm`
  - `settings.EnableVisionLlmGrpcHost`
  - `settings.EnableLlamaCppTranslation`
  - `settings.EnableVisionLlmSharedLocalTranslation`
  を満たすとき `true`
- `AppSettings` の追加候補:
  - `GraphicsResourceBudgetProfile` もしくは `HostVramBudgetProfile`
- 初期の weight 例:
  - `WinRT` = 0
  - `NDL` = no budget (0)
  - `Paddle (cpu)` = 0
  - `Paddle (gpu)` = 1
  - `PaddleVL` = 6
  - `VisionLLM` = 4
  - `Llama` = 3
- 初期の budget limit 例:
  - `LowVram` = 4
  - `Balanced` = 6
  - `HighVram` = 8
  - `UltraVram` = 10

7. ルール案
- 主 OCR:
  - `OcrEngine` から決まる host は `Required=true`
- helper OCR:
  - `VisionGeometryHybridBaseEngine` に応じて `HelperOcr` を追加
  - `Required=false`
  - ただし helper が GPU 実行なら budget に含める
- 翻訳 host:
  - `UsesVisionLocalTranslation(settings) == true`
    - `VisionLLM` 自前翻訳を使うため、純粋な `Llama` host は required budget に入れない
    - 必要なら optional 常駐としてだけ扱う
  - `UsesVisionLocalTranslation(settings) == false` かつ `EnableLlamaCppTranslation=true`
    - `Translation` host として `Llama` を追加する
    - `Required=false` を基本とする
- budget 超過時:
  1. `HelperOcr` を先に落とす
  2. 次に `Translation`
  3. `PrimaryOcr` は落とさない
- required host のみで超過する場合:
  - 設定変更を拒否する
  - ダイアログを表示する
  - 前回設定へ戻す
- これにより、今の `Vision shared translation なら Llama 停止` は、
  - `VisionLLM (4, Required)`
  - `Llama (3, Optional or not counted)`
  - budget が厳しければ `Llama` を落とす
  へ自然に置き換わる

8. 実装手順
- Step 1: 現行 host 判定の棚卸し
  - 各 host の role, weight, required/optional を固定表で定義する
  - `Paddle` は device によって weight を分ける
  - `UsesVisionLocalTranslation(settings)` を追加し、翻訳経路の判定を 1 箇所に寄せる
- Step 2: desired host set の導入
  - `ShouldLoad*` を直接 orchestrator に渡すのをやめ、まず desired host list を作る
  - `UsesVisionLocalTranslation(settings)` を見て `Llama` の required/optional/no-count を決める
- Step 3: VRAM budget 適用
  - optional host を優先順位つきで落とすロジックを追加する
- Step 4: required budget validation
  - 主 OCR + 翻訳など required host だけで超過する場合、UI で fail fast し、設定変更をキャンセルして前回設定へ戻す
- Step 5: reconcile へ移行
  - `StopHostsNoLongerNeeded` と `StopBeforeStartHostIds` を縮小または撤去する
- Step 6: ログと可視化
  - `stage=grpc_host_plan event=budget_decision ...`
  - `stage=grpc_host_plan event=budget_reject ...`
  - `stage=grpc_host_plan event=translation_route uses_vision_local_translation=yes|no ...`
  を追加する

9. 非機能要件チェック
- 性能:
  - 設定変更時のみの軽量判定であり、ランタイムコストは小さい
- セキュリティ:
  - 新しい外部 I/O なし
- 可観測性:
  - どの host が required / optional / dropped_by_budget / rejected_by_budget になったかログで追えるようにする
  - `UsesVisionLocalTranslation(settings)` の判定結果をログで追えるようにする
- 互換性:
  - 既存 settings を壊さず、内部ロジックだけ置き換える

10. リスクと緩和策
- Risk: 静的 weight が実際の VRAM 使用量とずれて、一部環境で不適切な stop が起こる
- Mitigation: まずは conservative に設定し、profile 単位で調整できるようにする
- Risk: helper OCR が落とされると Hybrid 精度が変わる
- Mitigation: helper は optional と明示し、ログに `dropped_by_budget` を出す
- Risk: `Paddle(cpu)` と `Paddle(gpu)` の扱いが settings とずれる
- Mitigation: budget weight は engine 名ではなく engine + device で決める
- Risk: required budget reject が多すぎると UX が悪化する
- Mitigation: reject は `PrimaryOcr + Translation` のような必須構成だけに限定し、helper 超過は自動縮退で吸収する
- Risk: VisionLLM self-translate 時に `Llama` の常駐有無が分かりにくくなる
- Mitigation: `UsesVisionLocalTranslation(settings)` を単独 helper 化し、budget と host plan の両方で同じ判定を使う

11. 影響範囲
- `Services/Application/ResourceHostFacade.cs` — 実装本体の再構成
- `Services/GrpcHost/GrpcHostDescriptor.cs` — `ShouldLoad` 依存を縮小する場合に調整
- `Services/GrpcHost/GrpcHostOrchestrator.cs` — desired plan ベースの reconcile へ寄せる場合に調整
- `Models/AppSettings.cs` — budget profile の設定追加
- UI 設定適用箇所 — budget reject 時のダイアログ表示と前回設定復元が必要
- `./.agent/changes.md` — 実装時の記録

12. Definition of Done
- host 常駐判定が `BuildDesiredHostSet + ApplyVramBudget + ValidateRequiredBudget + ReconcileHosts` の 1 経路で追える
- `ShouldKeep*Resident`, `UseVisionSharedLocalTranslation`, `ShouldStopLlamaAsUnused` の特例が大幅に減る
- VisionLLM helper OCR / local translation の組み合わせが budget 判定で説明できる
- `Paddle(cpu)` と `Paddle(gpu)` の weight 差を反映できる
- `UltraVram = 10` を含む budget profile を選べる
- `UsesVisionLocalTranslation(settings)` の判定で `Llama` の required budget 参加有無が決まる
- required budget 超過時に、設定変更がキャンセルされ、前回設定へ戻る
