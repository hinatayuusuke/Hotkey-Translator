# OCR 1% Low 最優先アーキテクチャ案

1. **概要（1–3行）**
- 本書は「OCR 機能は維持するが、ゲーム側の `1% low` を最優先にする」前提での実装方針を整理したアーキテクチャ案である。
- 中心思想は、OCR を速くすることではなく、OCR のコストをゲームの `Present` / render thread から追い出すことにある。
- そのため、capture・publish・OCR・overlay を同期パイプラインとして扱わず、`latest-only` の非同期パイプラインとして再定義する。

2. **ゴール / 非ゴール**
### ゴール
- ゲーム側 `Present` hot path から CPU publish・OCR 前処理・OCR 実行を外す。
- OCR はベストエフォート動作とし、古いフレームを処理し続けない。
- 翻訳表示まで含めて「少し遅れてもよいが、ゲーム描画を乱さない」構成にする。

### 非ゴール
- OCR 結果の完全リアルタイム追従を保証すること。
- 全フレームを欠損なく OCR すること。
- OCR 精度を最優先にして、描画負荷を許容すること。

3. **前提・仮定**
- OCR は最終的に CPU バッファを必要とする可能性が高く、readback 自体は完全には消えない。
- ただし readback / OCR / translation の実行場所をゲームプロセス外へ出せば、ゲーム側 `1% low` への悪影響は大きく下げられる。
- OCR は全フレーム処理よりも、`最新フレームだけを拾う` 方針の方が 1% low 優先設計に合う。

4. **設計原則**
### 原則 1: Hook は「描画に寄り添う」のではなく「最新フレームを通知する」だけにする
- Hook の責務は `capture issue + ready 判定 + latest publish` に限定する。
- OCR 前処理、OCR 推論、翻訳リクエスト、overlay レイアウト計算はゲームプロセス外で行う。

### 原則 2: OCR は latest-only にする
- OCR worker が忙しい間に新フレームが来たら、古い未処理フレームは捨てる。
- queue は FIFO 完走型ではなく、`常に最新 1 件だけ有効` な overwrite 型にする。

### 原則 3: OCR の起動条件を絞る
- 毎フレーム OCR しない。
- `ROI 差分あり`、`最低間隔経過`、`overlay 表示中`、`ホットキー要求あり` など、条件を満たした時だけ動かす。

### 原則 4: Overlay は OCR と独立に degrade できるようにする
- OCR が遅れても前回結果をしばらく維持する。
- OCR が止まっても Hook overlay 自体は動き続ける。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `HookAgent`
  - GPU capture
  - latest frame publish
  - overlay draw

- `CaptureHost`
  - shared memory / shared texture consumer
  - CPU bitmap 化
  - frame diff 判定
  - OCR request 発行

- `OcrWorker`
  - OCR 前処理
  - OCR 実行
  - latest-only 処理

- `TranslationWorker`
  - OCR 結果差分の正規化
  - 翻訳 API 呼び出し
  - 直近 overlay payload 生成

- `OverlayPublisher`
  - 最新 overlay payload を Hook 側へ publish
  - OCR/翻訳遅延時は前回 payload を維持

### 5.2 データフロー
1. Hook が capture 対象フレームを publish
2. host 側が最新 frame を 1 件だけ保持
3. diff 条件を満たす時だけ OCR worker に渡す
4. OCR worker は古い未処理 frame を破棄して最新だけ処理
5. translation worker も同様に古い未処理結果を溜めない
6. overlay publisher は最新確定結果のみ Hook へ送る

6. **キュー戦略**
### 6.1 Capture -> OCR
- queue は「長さ無制限の FIFO」にしない。
- 推奨は次のどちらか:
  - `single latest slot`
  - `ring size 2-3` で producer が古い slot を上書き

### 6.2 OCR -> Translation
- OCR 完了結果も全件翻訳しない。
- 直近の OCR 結果が前回翻訳結果と十分近いなら skip する。

### 6.3 Translation -> Overlay
- overlay publish は latest-only。
- OCR/翻訳が遅れても queue を貯めず、最新の完成品だけ上書きする。

### 6.4 禁止する構造
- フレームごとに OCR job を必ず積む
- OCR job が溜まっても全部処理する
- 翻訳 API キューを backlog 前提で伸ばす
- overlay publish をフレームごとに同期する

7. **Capture 方針**
### 7.1 最小構成
- DX11/Vulkan Hook は off-Present publish を先に実装する。
- shared memory 契約は当面維持し、ゲーム側 hot path から CPU publish を外す。

### 7.2 より強い 1% low 優先構成
- 可能なら shared texture を使い、CPU readback を host 側へ移す。
- これによりゲームプロセス側は GPU copy までで終えられる。
- ただし OCR が CPU を要求する限り readback 自体は host 側で残る。

### 7.3 結論
- shared texture は「OCR が不要になる」から有効なのではない。
- `CPU readback の場所をゲーム外へ移せる` から有効である。
- したがって 1% low 最優先なら shared texture は有力だが、実装コストが高いので第2段階以降に回す。

8. **OCR 実行方針**
### 8.1 latest-only OCR
- OCR worker が処理中なら次フレームは enqueue ではなく上書きする。
- 処理終了時に未処理 latest があればその 1 件だけ続ける。

### 8.2 OCR 起動条件
- `minimum_interval_ms`
- `ROI hash changed`
- `text presence heuristic changed`
- `user-triggered refresh`

### 8.3 OCR 前処理
- Hook 内ではやらない。
- resize、grayscale、threshold、deskew などは host/OCR worker 側へ寄せる。

### 8.4 OCR 品質とのトレードオフ
- 1% low 優先時は「少し古い OCR 結果」を許容する。
- 毎回の OCR より、前回結果の再利用と差分判定の方を優先する。

9. **Overlay 方針**
### 9.1 表示継続
- OCR が更新されなくても直近 overlay を一定時間維持する。
- WHY: OCR が間欠更新でも視覚的には十分成立するケースが多い。

### 9.2 degrade 方針
- OCR 遅延時は `loading` や `stale` を内部状態で持つが、ゲーム描画は止めない。
- overlay payload が更新できない時は前回 payload を維持し、空 publish を乱発しない。

### 9.3 レイアウト計算
- フォントフィットや block layout も host 側で計算し、Hook 側は描画だけに寄せる。

10. **実装ステップ**
- Step 1: DX11/Vulkan の off-Present publish を実装し、ゲームプロセス内の CPU publish を除去
- Step 2: CaptureHost に latest-only frame slot を導入
- Step 3: OCR worker を latest-only queue に切り替え、backlog 完走をやめる
- Step 4: OCR 実行条件に `minimum_interval + ROI diff` を導入
- Step 5: overlay publisher を latest-only にし、前回結果保持を強化
- Step 6: 必要なら shared texture bridge を検討し、readback を host 側へ移す

11. **非機能要件チェック**
- 性能
  - ゲーム側 `Present` hot path に OCR 由来 CPU コストが入らない
  - `GraphicsHookCaptureFpsLimit=1/5/15` で `1% low` を比較可能

- 安定性
  - OCR worker stall が Hook / overlay を巻き込まない
  - backlog でメモリ使用量が増え続けない

- 可観測性
  - `capture_dropped_latest`
  - `ocr_skipped_interval`
  - `ocr_skipped_no_diff`
  - `ocr_overwrite_pending`
  - `translation_overwrite_pending`
  - `overlay_payload_age_ms`

- 互換性
  - 既存 OCR provider を差し替え可能に保つ

12. **リスクと緩和策**
- Risk: latest-only にすると一部の短時間テキストを取りこぼす。
- Mitigation: 手動 refresh / low interval 設定 / 重要領域だけ interval を下げる設定を残す。

- Risk: OCR 更新が間欠になり、UI が古く見える。
- Mitigation: overlay に stale 許容時間を持たせ、必要時のみ更新する前提で UX を設計する。

- Risk: shared texture 導入時に host 側 GPU 管理が複雑化する。
- Mitigation: 第1段階では shared memory 維持、第2段階以降で分離計画を起こす。

13. **推奨判断**
- `1% low` 最優先なら OCR は「同期処理」ではなく「最新結果をたまに更新する補助機能」として扱うべきである。
- つまり守るべき SLA は `every frame OCR` ではなく、`game render thread must not wait for OCR` である。

14. **Definition of Done**
- [ ] Hook 側に OCR 前処理・OCR 実行・翻訳待ちが残っていない
- [ ] OCR queue が latest-only で動作する
- [ ] 古い frame / OCR / translation job を backlog 完走しない
- [ ] overlay が OCR 遅延時でも前回結果で継続表示できる
- [ ] `1% low` が従来構成より改善する
