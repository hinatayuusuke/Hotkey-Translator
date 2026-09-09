# Vulkan Hook パフォーマンス改善：現行コード比較と次のステップ

作成日: 2026-09-09（Asia/Taipei）  
比較基準: `922ec831a6992fb0bcfa6c1ffeba1b1158dc6f06`（調査開始時の作業ツリーに差分なし）  
元方針: [GraphicsHook OBS参照 パフォーマンス改善方針](GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md)

## 1. 概要

現行 Vulkan 実装は、delayed readback の CPU copy / publish を worker へ移す段階をすでに終えている。共有メモリも V2・2 slot へ移行し、C# consumer の mapping 再利用と sleep wait 廃止が入っているため、元方針の Step 1～5 をそのまま再実装する計画にはしない。

次は **worker / GPU resource の寿命保証と計測 → 完了回収の FPS 制限からの分離 → present 同期を設計したうえで overlay の CPU fence wait を除去** の順に進める。worker 内の二重コピー削減と stale frame 制御は、その後の独立した改善とする。

本書はコードの静的比較に基づく実装案である。ゲームでの FPS、1% low、遅延、競合の再現試験は今回未実施であり、改善率や障害の発生頻度は確定していない。

## 2. ゴール / 非ゴール

### ゴール

- 通常の delayed capture と overlay で、hook が追加する CPU 待機と present thread の tail latency を減らす。
- capture FPS が 1 / 5 / 15 のときも、GPU 完了済みフレームを次の capture 発行時刻まで放置しない。
- resize / detach / device 破棄で worker と GPU が使用中の resource を解放しない。
- 現行 FramePipe V2 と BGRA8 payload を維持し、コード上の改善と実ゲームでの効果を別々に検証する。

### 非ゴール

- shared texture bridge、OCR 全体、DX9 / DX11 / OpenGL の同時再設計。
- V1 reader の復活、新しい互換レイヤや自動 fallback の追加。
- 実測前の ring 拡大、SIMD 化、常駐 GPU polling worker の追加。
- 本タスクでの実行コード変更。本書に列挙する実装・テストは次の作業で行う。

## 3. 現状比較

以下の行番号は比較基準のもの。変更後は関数・型名を検索して追跡する。

| 元方針の論点 | 現行コードの根拠 | 判定と次の扱い |
| --- | --- | --- |
| Vulkan の CPU publish が Present に残る | `Native/HookAgentVulkan/VulkanPresentHook.cpp:894` の `PublishWorkerMain` が copy / RGBA→BGRA 変換と `WriteFrame` を実行。`:2659` の delayed 分岐は fence 確認後に enqueue | 通常の delayed 経路は実装済み。worker 新設は不要 |
| delayed 無効時の同期 capture | 同ファイル `:3035` に最大 1 秒の `vkWaitForFences`、`:3082` 以降に CPU copy、`:3114` に `WriteFrame` | `HT_HOOK_VK_DISABLE_DELAYED_READBACK` で選ぶ immediate 経路は残存。通常経路と区別して測定 |
| overlay の待機 | 同ファイル `:3035` の wait 後、`:3049` で `overlay_only` として return | delayed が有効でも capture 発行のない overlay フレームは同期経路。次段階の主要対象 |
| worker 所有権・世代・drain | 同ファイル `:83` の slot state、`:1074` の completion 回収、`:1136` の drain、`:1976` の再作成前 drain | 土台は実装済み。ただし idle 通知と GPU 完了保証は追加確認が必要 |
| 単一 payload / V1 維持 | `Native/HookCommon/HookIpcProtocol.h:13` と `:15`、`SharedFrameWriter.cpp:77`～`:103` | すでに V2・2 slot・`publishedSeq` / `slotSeq`。今後維持する契約は V2 |
| consumer が毎回 mapping を開く | `Services/GraphicsHookCaptureProvider.cs:309` の `TryGetAccessorLocked` | pid + map 名が一致する現在の 1 組を再利用。複数 map の辞書キャッシュではないが、安定した同一 map の reopen は解消 |
| `Thread.Sleep(5)` による待機 | 同ファイル `:200` の `TryReadBitmap` は最大 5 回の即時再読込と confirm read | 元方針の `TryWaitForInitializedHeader` / `TryWaitForNewFrame` は現行ファイルに存在しない |
| 古い cache の抑制 | 同ファイル `:425`、`:446`、`:459` | 同一 frameId の clone と writer race 時の cache 利用は残る。age 上限はなく、`TimestampQpc` を鮮度判定に使っていない |
| DX11 の worker 化 | `Native/HookAgentDx11/Dx11PresentHook.cpp:906` の `PublishWorkerMain` | worker は存在。DX11 全経路の完了認定や追加最適化は本書の対象外 |

V2 の複数バッファ化は OBS と同一の mutex 契約ではない。reader の confirm read は現在も必要であり、単に削除する施策にはしない。

## 4. 優先課題と理由

### P0-A: publish idle 通知と resource 寿命を先に固める

`EnqueuePublishRequestLocked` は queue lock 内で push した後、lock 外で `ResetEvent(publishIdleEvent)` を行う。worker も lock 内で idle を計算し、lock 外で `SetEvent` する（`:981`～`:993`、`:1184`～`:1203`）。この順序には次の競合が成立し得る。

- worker が新しい request を処理し終えて idle を通知した後に producer が Reset すると、queue は空でも drain が永久待機し得る。
- worker が古い idle 判定を保持したまま、producer の push / Reset 後に Set すると、未処理 request があるのに drain が完了したように見え得る。

これは静的なインターリーブ分析であり、実機再現済みという意味ではない。queue と active count の更新、idle event の Set / Reset を同じ queue lock の下で順序付け、drain は起床後に述語を再確認する。worker は `rt.mutex` を取得しない現在の構造を維持する。

さらに `DrainPublishQueueLocked` が待つのは CPU worker であり、`Pending` slot の GPU 完了ではない。`EnsureQueueGpuStateLocked` は drain 後に `DestroyQueueGpuState` を呼び、破棄関数自体には GPU fence 完了確認がない（`:1976`、`:846`）。再作成・detach の全呼出経路で GPU 完了を保証するか、完了まで旧 resource を保持する必要がある。世代番号照合は completion の誤適用を防ぐが、worker が読む pointer や GPU resource の寿命を単独では保証しない。

### P0-B: 測定の空白を埋める

`present_perf` は `SubmitPresentWorkLocked` 内の間引きログである。`rt.mutex` の取得待ち、hook 全体、元の `vkQueuePresentKHR` の所要時間、worker 内 copy / write は別計測になっていない。delayed 経路で `cpuCopyMs` / `writeMs` が小さくても worker の負荷が消えた証拠にはならない。

`captureIssueCount` / `capturePublishCount` / `captureDeferCount` / `captureBusyCount` は存在するが、現行ファイルでは集計値のログ出力に使われていない。worker の enqueue / 開始 / 完了時刻と合わせて、期間集計として出す。

### P1: GPU 完了回収を capture 発行間隔から切り離す

`ShouldCaptureNowLocked` は `lastCaptureIssueQpc` を基準に発行間隔を制限する。一方、pending fence の確認も `if (delayedReadbackEnabled && shouldCapture)` の中にある（`:1749`、`:2659`）。overlay なしではその前の `:2574` で return し、overlay ありでも `shouldCapture == false` なら pending を確認しない。

そのため、例えば capture 1 FPS で発行直後に GPU が完了しても、回収は通常、約 1 秒後の次回発行可能時刻になる。5 FPS / 15 FPS でもそれぞれ約 200 ms / 67 ms の発行間隔に回収が結び付く。これは処理順からの予測であり、実測値ではない。worker completion の回収が毎 Present に行われていても、GPU pending → worker enqueue の遅れは解消しない。

### P2: overlay の非同期化には present 同期の設計が必要

現行 delayed / immediate の `VkSubmitInfo` は command buffer と fence を指定する一方、wait / signal semaphore を設定していない（`:2844`、`:3019`）。`Hook_vkQueuePresentKHR` は先頭 swapchain のみを処理し、最後に元の `presentInfo` をそのまま渡している（`:3723`～`:3741`）。

ここで CPU wait だけを削除してはならない。Vulkan 仕様は present 前のコマンド完了に semaphore による同期を要求しているため、既存 delayed 経路も含めて game render → hook copy / overlay → present の依存を監査する。この指摘は API 契約と現行コードからの判断であり、特定ゲームでの描画障害を確認したものではない。[Khronos: vkQueuePresentKHR](https://docs.vulkan.org/refpages/latest/refpages/source/vkQueuePresentKHR.html)

## 5. 提案アーキテクチャとインターフェース

### 5.1 完了回収と worker

`PollCompletedCapturesLocked(rt, queue, gpu)` 相当の内部関数を抽出する。既存 queue state に対して毎 Present、capture 発行判定や「何もしない」return より先に実行する。新しい frame を発行しない回でも fence を非ブロッキングで確認する。Present 自体が止まる場合の回収は今回の対象外とする。

状態遷移は `Free → Pending → Publishing → Free` を基本とし、未使用の `ReadyToPublish` を使うなら、その段階の所有者と取消可能条件を明確にする。初回は最古 pending の確認という現在の選択方針を維持し、最新優先の破棄は別変更に分ける。

worker request / completion に `captureSubmitQpc`、`enqueueQpc`、`workerStartQpc`、`writeEndQpc` と計測結果を持たせる。これらは native 内部データとし、FramePipe の layout / version は変更しない。`VK_NOT_READY` は保留、enqueue / write 失敗は明示ログ、fence error は通常の完了扱いで再利用せず、失敗状態と終了処理へ分ける。

### 5.2 overlay submit と present

`SubmitPresentWorkLocked` の現在の `bool` 戻り値だけでは、present に渡す同期情報を表現できない。内部の入力に元の present wait 情報を渡し、戻り値を「hook submit の有無・結果・present が待つ semaphore」を表す構造へ変更する案とする。

正常時の順序は以下とする。

1. hook GPU submit が元の present wait semaphores を待つ。
2. capture が必要なら overlay 描画前に copy し、overlay を描画して image を present layout に戻す。
3. hook 完了 semaphore を signal する。
4. 元の `VkPresentInfoKHR` のローカルコピーで wait 配列だけを差し替え、元の Present を呼ぶ。`pNext`、全 swapchain、image index、`pResults` は保持する。

元の binary semaphore を hook submit と Present の両方で wait しない。hook が submit しない正常な回は元の wait 配列をそのまま使う。submit 失敗後は wait の消費状態に基づく明示的なエラー処理を設計し、元 Present の再試行や同期経路への自動切替は追加しない。

overlay 用 command buffer / fence を CPU readback slot と独立して再利用できる構造にする。busy 時は GPU 完了を CPU で待たず、その回の新規 hook 作業を skip して理由を記録する。ImGui が使用する buffer の寿命も含め、in-flight command buffer を reset しない。

present wait semaphore は submit fence の完了だけでは再利用可能と判定できない。swapchain image 単位の管理と acquire による完了保証を基本候補とし、multi-swapchain、queue family ownership、swapchain 破棄時の回収を先に設計する。追加 extension が有効という仮定は置かず、対応範囲が決まるまで非同期 overlay の通常化は行わない。[Khronos: Swapchain Semaphore Reuse](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html)

### 5.3 worker 内 copy と consumer の後続改善

BGRA の場合、現状は `stagingMapped → publishScratch → mapping` の二重コピーである。計測で寄与が大きければ、寿命を保証した mapped pointer を `WriteFrame` に直接渡し、前半の copy を省く。RGBA→BGRA 変換は scratch を維持し、SIMD 化や writer API の拡張は初回に含めない。共有メモリへの copy 自体は残る。

consumer の `TimestampQpc` は現在、Vulkan delayed 経路では enqueue 時点の `publishQpc` であり、GPU copy 発行時刻ではない。まず native 内部の capture age を測る。consumer の鮮度上限を実装する場合は、timestamp の意味を明示して producer / consumer を合わせ、同一 frameId の clone で鮮度を更新しないようにする。現在の `UpdateCache` は読み取り成功ごとに時刻を更新するため、その時刻だけでは元画像の古さを判断できない。

## 6. 実装手順

| 順序 | 小さく分ける変更 | 完了条件 |
| --- | --- | --- |
| Step 1a | idle event と queue / active 述語の原子的一貫性、drain の失敗確認 | worker 完了と enqueue を交差させる再現テストで、空 queue の永久待機と早すぎる解放が起きない |
| Step 1b | 再作成・detach・device / swapchain 破棄の GPU / worker 寿命監査と修正 | GPU 未完了 / worker 使用中の resource を解放せず、世代切替後の旧 completion を適用しない |
| Step 2 | hook 全体・worker・capture age・counter の期間集計 | 現行に対する比較基準を保存。ログ無効時の追加負荷も確認 |
| Step 3 | pending 回収を発行 FPS gate から分離 | 1 FPS でも GPU 完了後の次の Present で enqueue でき、発行数は 1 FPS 相当を維持 |
| Step 4a | 既存 delayed を含む semaphore chain と resource 再利用条件の設計・検証 | game wait を二重消費せず、present 前の hook 完了を保証。失敗・multi-swapchain の扱いを確定 |
| Step 4b | overlay 用 submit resource を分離し CPU fence wait を外す | 通常 delayed + overlay 経路に hook が追加する blocking fence wait がなく、同期検証に合格 |
| Step 5 | 測定で必要なら BGRA の中間 copy 削減、続いて backlog 制御 | 色・stride・frameId・寿命を維持し、worker 時間 / capture age が改善 |
| Step 6 | 別変更で consumer の鮮度上限と cache 更新条件を整理 | 同じフレームの繰返し取得で鮮度が延命されず、stale を明示 skip |

既存の `HT_HOOK_VK_DISABLE_DELAYED_READBACK` は比較用の明示設定として扱い、実行中の動的切替を新たに保証しない。新しい feature flag は初回に増やさず、Step ごとに検証・マージし、問題時は該当変更を revert して既知の DLL へ戻す。

backlog は現在 FIFO で、明示的な最新優先・上限判定はない。ただし未完了 slot 数に制約されるので、単純に「無制限に増える queue」とは評価しない。上限や最新優先を追加する場合も、GPU が使う `Pending` と worker が読む `Publishing` をその場で Free にしてはならない。破棄対象は所有権を安全に回収できる未着手 request に限定する。

## 7. 計測と検証

### 指標

| 対象 | 追加 / 整理する指標 | 用途 |
| --- | --- | --- |
| Present | hook entry～original 呼出前、`rt.mutex` 取得待ち、original 呼出時間、hook 全体の p50 / p95 / p99 | hook 負荷とゲーム / driver 側の Present 待機を分離 |
| GPU readback | 発行～fence ready を観測するまで、発行数、defer / busy 数 | polling 遅延と slot 不足を確認。観測時間を純粋な GPU 実行時間とは呼ばない |
| worker | enqueue～開始、copy / 変換、writer mutex 待ち、write、queue 深さ / active 数 | CPU copy、publish backlog、lock 競合を切り分け |
| 鮮度 | capture submit～write 完了、consumer 取得時 age、同一 frameId / cached reuse 数 | throughput 改善で古い画像が増えていないか確認 |
| ライフサイクル | recreate / worker drain / GPU retirement の時間、世代、失敗理由 | 通常 Present と resize / 終了時の停止を別集計 |

各 Present の同期ファイル出力は避け、固定容量の集計や間引いた診断を使う。現在の間引き `present_perf` の行だけから全フレームの p99 を推定しない。低 capture FPS では fence polling 回数が増えるため、`captureDeferCount` は回数だけでなく発行数・観測期間も一緒に記録する。

### 実ゲーム比較

- hook なし / 現行基準 / 各 Step 後を、同じゲーム・場面・解像度・GPU / driver・VSync / FPS 上限で比較する。
- capture FPS 1 / 5 / 15 × overlay 無効 / 有効。有効時は表示ブロックなしと常時ありを区別し、特に「60 FPS 描画・capture 1 FPS・overlay 常時あり」を優先する。
- 1080p / 4K、対応する BGRA / RGBA、ring 既定 3 を基本とし、負荷試験では 2 / 8 も確認する。設定範囲は `ResolveCaptureRingSize` の現行 clamp に合わせる。
- 同条件でウォームアップ後に 60 秒以上を 3 回計測し、平均 FPS、同じ算出方法による 1% low、frame time p99、hook p99、capture age を保存する。改善目標値は Step 2 のばらつきを見て決め、未計測の割合を約束しない。
- Validation Layers / synchronization validation による正しさの試験と、検証レイヤを外した性能試験を分ける。

### 回帰・失敗試験

- GPU completion と worker completion を意図的に遅らせ、slot 飽和時も通常 Present が待たずに進むことを確認する。
- enqueue と idle 通知の競合、write 失敗、fence error、submit 失敗を注入し、待機取りこぼし・二重解放・使用中 slot 再利用がないことを検証する。
- resize、解像度変更、Alt+Tab、swapchain 再作成、detach / 再 attach、device lost、ゲーム終了を GPU / worker の未完了状態で試す。
- multi-swapchain、graphics / present queue の構成差、present の `OUT_OF_DATE` / `SUBOPTIMAL` と `pResults`、semaphore 再利用を検証する。
- frameId の単調性、画像の破損・色・stride、overlay の OCR 画像への混入、V2 reader の race 時の挙動を確認する。

将来の実装時は `Cpp_Build.md` と既存 CMake 構成に沿って、次を実行する。今回の文書作成では実行していない。

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release --target HookAgentVulkan
cmake -S Native -B Native/build_x86 -A Win32
cmake --build Native/build_x86 --config Release --target HookAgentVulkan
```

Vulkan SDK が見つからない場合、CMake は target を skip する実装なので、configure 成功だけで合格としない。x64 / x86 両 target のビルド成立を確認する。共通 writer / protocol を変更する場合は DX11 / DX9 も、consumer を変更する場合は C# のビルドと capture 回帰も対象へ加える。

## 8. 非機能要件・リスクと緩和策

| 観点 / Risk | Mitigation |
| --- | --- |
| idle 通知と queue が不一致になり停止 / 早期解放 | 同一 lock で述語と event を更新し、起床後にも述語を確認。timeout だけを足して使用中 memory を解放しない |
| GPU / worker の使用中 resource を resize で破棄 | CPU drain と GPU 完了を別管理。通常 Present の待機除去と teardown 時の必要な完了待ちを混同しない |
| semaphore 二重 wait / 早すぎる再利用 | 元 wait の消費先を一箇所にし、present 完了を submit fence と区別。同期検証を Step 4 の必須条件にする |
| 計測や毎 Present の poll 自体が負荷になる | 固定上限の走査・集計、診断無効時との比較。不要な GPU submit や sleep は追加しない |
| capture が古くなる / drop で OCR 品質が落ちる | age と発行 / publish / drop を同時に計測。鮮度上限は capture 間隔と処理予算から決める |
| IPC / セキュリティ・データ影響 | V2 layout、BGRA8、payload bounds を維持。ログへ画像本文や翻訳内容を出さず、コピー省略時も pointer と bytes の検証を維持 |
| 切り戻しで契約がずれる | native 内部変更を先行し、V2 契約を維持。timestamp の意味を変更する段階は consumer と合わせてレビュー |

## 9. 影響範囲・移行

- `Native/HookAgentVulkan/VulkanPresentHook.cpp`: Step 1～5 の中心。worker queue、fence 回収、present 同期、overlay resource、計測、破棄経路。
- `Native/HookAgentVulkan/CMakeLists.txt`: テスト target / source を追加するときだけ変更候補。
- `Services/GraphicsHookCaptureProvider.cs`: Step 6 の鮮度判定と cache 更新。Step 1～5 のための mapping cache 再実装は不要。
- `Native/HookCommon/SharedFrameWriter.cpp` / `.h`、`HookIpcProtocol.h`: 契約確認対象。BGRA pointer の直接渡しだけなら既存 `WriteFrame` を使えるため変更不要。
- `Doc/GraphicsHook_Vulkan_Performance_Next_Steps.md`: 本書。元方針書は履歴として維持する。
- `.agent/changes.md`: 将来、実行コード / 設定を変更したタスクの完了時に規定形式で追記する。本タスクは文書のみの追加。

初期 Step は FramePipe V2 と設定を維持するためデータ移行不要。shared texture 化はこれらを計測してなお不足する場合に限り、別計画とする。

## 10. Definition of Done

- [ ] worker idle の取りこぼし / 誤通知を再現テストで防止し、GPU 未完了 resource の解放条件を明確にした。
- [ ] capture FPS の発行 gate と pending 回収が分離され、1 FPS でも回収が次の 1 秒周期を待たない。
- [ ] 通常 delayed / overlay 経路で hook が追加する blocking GPU wait と CPU payload publish が Present から外れている。明示 immediate 設定は別集計にした。
- [ ] semaphore chain、command / ImGui buffer 再利用、resize / detach が同期検証を通る。
- [ ] worker copy / write と hook p99、capture age、1% low の比較結果があり、改善が測定のばらつきと区別できる。未改善なら原因を記録して次段階へ無条件に進まない。
- [ ] x64 / x86 ビルドと画像・overlay・終了時の回帰試験を通過した。
- [ ] consumer 鮮度変更を実施した場合は timestamp の意味と stale 上限を明記し、同一 frameId で鮮度が延命されない。

## 11. 未確定事項

- 実ゲームで支配的なのは overlay wait、worker copy、GPU copy、mutex 待ちのどれか。Step 2 の測定で確定する。
- 対象タイトルの swapchain / queue 構成と、present semaphore の安全な破棄に必要な機能が実際に有効か。Step 4a の導入条件として確認する。
- OCR が許容する stale 上限。1 FPS の通常間隔を誤って全拒否しないよう、Step 6 で処理予算と合わせて定義する。

## 12. 今回の確認範囲

指定の元方針書を UTF-8 で読み、Vulkan Hook、SharedFrameWriter / IPC、C# capture provider、DX11 worker の存在、CMake / build 手順を照合した。Serena のシンボル参照と `rg` / 行番号付きコード読取を併用し、Vulkan の present 同期・semaphore 再利用条件は上記 Khronos 一次資料で確認した。OBS の詳細は指定方針書の記述を比較の起点とし、OBS 最新版との再比較は行っていない。

実行コード・設定・既存文書は変更していない。ビルド、ゲーム起動、性能測定、Validation Layers、競合再現試験は未実施であり、本書の完了条件は今後の実装に対する条件である。

## 13. 実装状況（2026-09-09、上記の調査後に実施）

この節は実装タスクの結果であり、1～12節の比較基準時点と区別する。対象 SDK は `G:/Development-Cache/VulkanSdk`（headers 1.4.341）。x86 はアンチウイルス検出のため、ユーザー指示により以後のビルド・テストをスキップする。

| Step | 実装・確認した内容 | 残る検証 / 作業 |
| --- | --- | --- |
| 1a | enqueue と worker completion の idle event 操作を queue lock 内へ移動。drain が述語を再確認し、worker 終了・待機失敗を検出。active writer を保持する試験と2,000回の enqueue / drain 試験に合格 | 実ゲームでの長時間運転 |
| 1b | immediate submit の未完了状態を追跡し、再作成・device / swapchain 破棄・reset / detach の前に hook GPU fence 完了を確認。fence / submit error 時は queue を失敗状態にして再利用を止める | 実ゲームの resize / Alt+Tab / detach、WSI と ImGui の全構成の検証 |
| 2 | `perf_window` と `pipeline_counts` を追加。hook / mutex / original Present、worker queue / copy / writer lock / write、capture age、GPU 完了観測、drain / retirement を計測 | ゲームの変更前後比較、1% low の基準値・改善率は未測定 |
| 3 | `PollCompletedCapturesLocked` を capture FPS 判定と早期 return より前に実行。実際の `SubmitPresentWorkLocked` を使い、capture 1 / 5 / 15 FPS で次の60 FPS tick時に新規発行せず完了回収できることを確認 | Present 自体が止まった場合の回収は元の非ゴールどおり対象外 |
| 4a / 4b | 未実装。元の present wait 配列は変更しておらず、overlay-only / immediate の CPU wait は残る | 5.2節の「対応範囲が決まるまで非同期 overlay の通常化は行わない」に従い、present semaphore の再利用・破棄、multi-swapchain / queue family の扱いを検証してから変更する |
| 5 | 条件付き施策として未着手。BGRA の中間 copy と FIFO は維持 | worker copy の実測に基づく判断 |
| 6 | 別変更として未着手。V2 header と consumer の timestamp / cache 契約は維持 | stale 上限と timestamp の意味を確定して実装 |

実装中に判明した追加修正:

- `CreateCaptureSlotResources` / `CreateStagingResources` の `stagingBytes` を allocation size ではなく画像の必要 bytes に修正。allocator padding を payload として読まないようにし、必要 bytes との比較が毎回不一致になって GPU state を再作成する問題も防ぐ。
- copy 後に `TRANSFER_WRITE → HOST_READ` の memory barrier を追加。HOST_COHERENT の選択と GPU fence 完了確認に加えて、CPU 読み出しへの依存を明示する。[Khronos: CPU read back synchronization example](https://docs.vulkan.org/guide/latest/synchronization_examples.html#_cpu_read_back_of_data_written_by_a_compute_shader)

### x64 の検証結果と再実行

- `HookAgentVulkan` / `HookAgentVulkanTests` の Release ビルド: 成功。
- CTest `VulkanReadback`: 成功。低 FPS の発行 / 回収分離、BGRA / RGBA の V2 payload、worker 所有権、idle 遷移、GPU retirement の順序と失敗、publish 失敗、旧世代 completion、worker 終了、計測時刻の逆順到着を確認。
- `HookAgentVulkanTests --gpu`: NVIDIA GeForce GTX 1080 で成功。Validation Layers と synchronization validation を有効にし、実 staging buffer の書込み → fence polling → worker publish を12回実行。画像8 bytesと確保時のpaddingを区別し、破棄まで validation error 0を確認。
- この GPU 試験は staging / worker の試験であり、swapchain Present、overlay、ゲーム内の性能測定に合格したことは意味しない。
- x86 は指示前にコンパイル・リンクまで確認したが、テスト起動が環境側で拒否された。以後はスキップし、セキュリティ設定や除外設定は変更していない。

```powershell
cmake -S Native -B Native/build -A x64 -DHT_VULKAN_SDK_ROOT_OVERRIDE=G:/Development-Cache/VulkanSdk -DHT_VULKAN_BUILD_TESTS=ON
cmake --build Native/build --config Release --target HookAgentVulkan HookAgentVulkanTests
ctest --test-dir Native/build -C Release --output-on-failure
```

GPU 試験は SDK の validation layer が利用可能な環境で `Native/build/HookAgentVulkan/Release/HookAgentVulkanTests.exe --gpu` を実行する。今回の試験プロセスでは `VK_LOADER_LAYERS_DISABLE=~implicit~` を指定し、ゲームランチャー等の implicit overlay layer を試験へ混在させていない。通常アプリの設定は変更しない。

### 計測ログの読み方

既存の性能診断設定 `kConfigFlagEnablePerfDiagLog` を有効にすると、新しい期間集計を出す。無効時は追加の worker 計時・期間集計を行わない。

- `perf_window`: 約2秒ごと、metric ごとの `count` / `retained` / `p50Ms` / `p95Ms` / `p99Ms` / `maxMs`。保持上限は各metric 2,048件で、quantile は保持した直近サンプル、count / max は報告期間全体。ログ整形・出力自体の時間は `hook_total` の終端後なので、この値に含めない。
- `gpu_observed`: GPU 発行から fence ready の観測まで。純粋な GPU 実行時間ではない。
- `capture_age`: GPU copy 発行時刻から worker write 完了まで。V2 の timestamp の意味を変更せず、native 内部だけで測定。
- `pipeline_counts`: issue / publish / defer / busy の累積値、現在の queue depth / active、前回報告以降の queue peak、FPS と delayed の設定。累積値の差分を観測期間とともに比較する。
- fence / submit error 後は、その queue の resource が再作成されるまで capture / hook overlay の新規処理を止める。worker / GPU retirement 失敗はログへ明示し、使用中 memory を解放したことにはしない。

次に必要なのは、対象ゲームで上記ログと frame time を取得し、Step 4 の WSI 同期設計を検証すること。1% low 改善や全計画の完了は、現段階では未確認である。
