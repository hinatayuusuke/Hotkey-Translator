# Resource Host Runtime Budget Eviction Plan

1. 概要
- OCR / 翻訳 host の常駐判定を、事前に複雑な組み合わせ表へ落とすのではなく、`host 起動要求時の budget 充足` と `不要 host の runtime eviction` で扱う。
- 方針は単純で、`今必要な host は止めない`、`不要な host の中から weight が大きい順に止める`、それでも足りなければ reject する。

2. ゴール / 非ゴール
- ゴール:
  - 既存の `ShouldLoad*` / resident 判定 / 特例停止を減らし、runtime の budget 調停へ一本化する。
  - Profile ごとの VRAM budget に応じて、未使用 host を自動的に残す / 落とす動作に統一する。
  - `Paddle`, `NDL`, `Llama`, `VisionLLM`, `PaddleVL` の常駐共存を、例外ルールではなく budget と現在使用状況で説明できるようにする。
- 非ゴール:
  - 実 VRAM 使用量のリアルタイム計測。
  - OCR / 翻訳アルゴリズム自体の変更。
  - `WinRT` のような non-host engine を host budget 管理対象にすること。

3. 前提・仮定
- 主 OCR は今後も `OcrEngine` の単一選択である。
- ただし host 常駐は主 OCR の単一選択とは独立であり、budget の範囲内なら複数 host が残ってよい。
- `WinRT` は host を持たないため budget 対象外。
- `NDL` は CPU 実行前提で budget weight は 0 とする。
- `PaddleOCR` は `PaddleDevice` が GPU の時だけ weight 1、CPU の時は 0 とする。
- `VisionLLM` は weight 4、`Llama` は 3、`PaddleOCR-VL` は 6 を初期値とする。
- `GraphicsResourceBudgetProfile` は `LowVram=4`, `Balanced=6`, `HighVram=8`, `UltraVram=10` とする。
- `VisionLLM` の自前翻訳を使う場合は、純粋な `llama_grpc` を required host としては見なさない。

4. 現状整理
- 現行の問題は、host 常駐と停止のルールが以下に分散していること:
  - `ShouldLoad*`
  - `ShouldKeep*Resident`
  - `StopBeforeStartHostIds`
  - `UseVisionSharedLocalTranslation`
  - `ShouldStopLlamaAsUnused`
- この構造だと:
  - `Paddle` と `NDL` を残したい
  - `VisionLLM` の helper OCR を併用したい
  - `Llama` は場合によって落としたい
  が、個別特例の積み上げになる。
- また、切替時に「今使っていないが残しておきたい host」が `stop_unused` で落ちやすい。

5. 提案アーキテクチャ
- 中心概念を `Desired resident set` ではなく、`Runtime start request` に置く。
- シーケンス:
  1. 設定と現在の route から「今必要な host」を列挙する
  2. その host が未起動なら、起動前に budget を確認する
  3. budget が足りなければ、実行中 host のうち `今不要な host` だけを stop 候補にする
  4. 候補を `Weight DESC` で並べ、足りるまで stop する
  5. それでも足りなければ reject する
  6. 足りたら要求された host を起動する
- つまり、host 常駐は「明示的に keep する」のではなく、「未使用でも budget を圧迫しない限り残る」という形にする。

6. インターフェース設計
- 新規 helper / model 案:
  - `HostRuntimeState`
    - `HostId`
    - `Weight`
    - `IsRunning`
    - `IsRequiredNow`
    - `CanStop`
  - `BuildRequiredHosts(AppSettings settings)`
    - 現在の OCR / helper / translation route に必要な host 集合を返す
  - `EnsureBudgetForHosts(AppSettings settings, IReadOnlySet<string> requiredHostIds)`
    - budget を満たすまで未使用 host を stop する
  - `GetRunningHostWeight(string hostId, AppSettings settings)`
  - `GetBudgetLimit(AppSettings settings)`
  - `UsesVisionLocalTranslation(AppSettings settings)`
- `AppSettings`:
  - `GraphicsResourceBudgetProfile` を保持する
- ログ:
  - `stage=grpc_host_plan event=budget_request ...`
  - `stage=grpc_host_plan event=budget_evict host=... weight=... reason=unused ...`
  - `stage=grpc_host_plan event=budget_reject ...`
  - `stage=grpc_host_plan event=translation_route uses_vision_local_translation=yes|no ...`

7. ルール案
- weight:
  - `WinRT = 0`
  - `NDL = 0`
  - `Paddle(cpu) = 0`
  - `Paddle(gpu) = 1`
  - `Llama = 3`
  - `VisionLLM = 4`
  - `PaddleVL = 6`
- budget 超過時の stop 順序:
  1. `今不要な host` のみ stop 候補にする
  2. 候補を `Weight DESC` で並べる
  3. 同 weight の host は任意順でよい
  4. 足りるまで stop する
  5. `今必要な host` は stop しない
- reject 条件:
  - 必要 host だけで budget 超過する場合
  - または未使用 host を全部止めても budget が足りない場合
- `VisionLLM` 自前翻訳:
  - `UsesVisionLocalTranslation(settings) == true` の時は、`llama_grpc` を現在必要な host に入れない
  - 必要なら未使用の pure `Llama` host は残っていてよいが、budget 超過時の stop 候補になる

8. 実装手順
- Step 1: host metadata の整理
  - host ごとの weight 算出関数を用意する
  - `UsesVisionLocalTranslation(settings)` を単独 helper として定義する
- Step 2: required host 列挙へ寄せる
  - `BuildRequiredHosts(AppSettings settings)` を導入する
  - 主 OCR / helper OCR / translation の「今必要」だけを判定する
- Step 3: runtime budget eviction 実装
  - `EnsureBudgetForHosts(...)` を追加し、未使用 host を weight 順で stop する
- Step 4: host start 経路へ統合
  - `EnsureResourceHostsAsync(...)` で host 起動前に `EnsureBudgetForHosts(...)` を必ず通す
- Step 5: 旧特例の整理
  - `ShouldKeep*Resident` を削減する
  - `StopBeforeStartHostIds` を原則撤去する
  - `UseVisionSharedLocalTranslation` / `ShouldStopLlamaAsUnused` を `UsesVisionLocalTranslation` + runtime eviction へ吸収する
- Step 6: reject と UI
  - 必要 host だけで足りない場合は設定変更を reject し、前回設定へ戻す

9. 非機能要件チェック
- 性能:
  - budget 判定は host 起動要求時だけで軽い
- 可観測性:
  - 何を要求し、何を stop し、なぜ reject したかをログで追える
- 互換性:
  - `settings.json` の OCR / 翻訳設定は維持し、内部の host 管理ロジックだけ差し替える
- 運用:
  - 未使用 host が残っていても、budget 圧迫時に自動で整理されるため、ユーザーが細かく stop しなくてよい

10. リスクと緩和策
- Risk: 未使用でも warm に残したい host が budget 圧迫時に落ちる
- Mitigation: それは設計意図と割り切り、ログで `budget_evict` を可視化する
- Risk: 重い host を優先 stop すると、再起動コストが高くなる
- Mitigation: 今回は simplicity を優先し、同 weight の詳細優先度は持たない
- Risk: 設定変更時だけでなく通常運用中も host stop が発生しうる
- Mitigation: stop 条件を `今不要` に限定し、required host は絶対に落とさない

11. 影響範囲
- `Services/Application/ResourceHostFacade.cs` — 実装本体
- `Services/Application/SettingsUiController.cs` — reject 時の settings restore
- `MainWindow.xaml.cs` — reject ダイアログと settings 差し戻し UI 同期
- `Models/AppSettings.cs` — budget profile 設定
- `Models/GraphicsResourceBudgetProfile.cs` — profile enum
- `Services/Settings/AppSettingsValidator.cs` / rules — profile 正規化
- `./.agent/changes.md` — 実装時の記録

12. Definition of Done
- host 起動要求のたびに budget 判定が走る
- budget 超過時は `今不要な host` のみが stop 候補になる
- stop 順序は `Weight DESC` で説明できる
- required host は stop されない
- `Paddle`, `NDL`, `Llama`, `VisionLLM` は budget が許す限り共存できる
- `VisionLLM` 自前翻訳時に pure `llama_grpc` を required host へ入れない
- 必要 host だけで budget 超過する設定は reject され、前回設定へ戻る
