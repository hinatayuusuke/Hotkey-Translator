「単一プロセス・GPUファースト」方針を、現行コード（GDIキャプチャ＋ROI切り出し＋pHash＋WinOCR＋Overlay）へ“壊さず”段階導入するための **実装案** と、環境差分で詰まらない **失敗しないロードマップ**です。現状の実装前提は `CaptureManager` が GDI `CopyFromScreen` を主経路にし（段階移行を意図したコメントもあり）、`PipelineOrchestrator` が ROI crop→pHash→OCR→差分翻訳→Overlay を一括で実行しています。  

---

## 0) 現状（ベース）と「変えない境界」

* **パイプラインの責務**は現状のまま維持：
  ROI決定（`AppSettings.Roi`）→ crop（`BitmapHelper`）→ pHash（`PhashService`）→ OCR（`OcrEngine`）→ 差分（`OcrDiffService`）→ 翻訳（`GeminiClient`＋Cache）→ Overlay更新（`OverlayPresenter`）。    
* **変える境界（差し替え点）**は「キャプチャ入力」と「ROI前処理」のみ：
  現行では `CaptureManager.Capture()` が `Bitmap` を返すため、ここを **抽象化**して WGC/DXGI/GDI を差し替え可能にするのが安全です。 

---

## 1) 実装案（単一プロセス・GPUファーストを“現実的に”落とす）

### 1-1. コンポーネント設計（最小の差分で導入）

#### A. CaptureCore（WGC標準＋DXGIフォールバック）

* **インターフェース化**（例）：`ICaptureProvider`

  * `TryGetFrame(...) -> CapturedFrame`
  * `CapturedFrame` は当面 **(Texture or Bitmap)＋Bounds＋Timestamp** を持つ。
* **Provider実装**

  1. `WgcCaptureProvider`（標準）
  2. `DxgiDuplicationProvider`（排他FS/黒画面/オーバーレイ破綻の救済：モニタ単位）
  3. `GdiCaptureProvider`（最後の保険：現行コードをそのまま移植）
* **選択戦略**

  * “学習”を実装：アプリ（プロセス名/ウィンドウクラス）単位に最後に成功した Provider を記憶し、初手から選ぶ。
  * 黒判定連続時は Provider をクールダウンして切替。

#### B. GpuPreprocess（ROIをGPUで小さくする）

* 入力：フルフレーム `ID3D11Texture2D`
* 出力：**小さいROIテクスチャ**（例：幅上限 1024、必要なら縮小）
* 実装：`CopySubresourceRegion` で ROI を小テクスチャへコピーし、縮小は（段階的に）

  1. まずは `CopySubresourceRegion` のみ
  2. 次に Direct2D / compute shader / simple shader でリサイズ＋グレースケール＋二値化

#### C. FrameGate（黒判定＋変化判定の強化）

* 既に pHash による「変化が小さい→OCRスキップ」が存在するため、ここを **Gate層**として独立させるのが自然です。  
* 追加する判断：

  * **黒判定**：平均輝度＋分散（低い＆低いがN回）→ Provider切替
  * **スロットリング**：OCRが詰まっているなら古いフレームを捨てる（最新優先）

#### D. OcrStage（CPUへ戻すのは小ROIだけ）

* 現状 `OcrEngine` は `Bitmap -> SoftwareBitmap` へコピーして WinOCR に渡しています。 
* GPU導入後の現実的経路：

  * ROIテクスチャを staging にして `Map` → BGRA byte[] → `SoftwareBitmap`
  * ここ以外でCPUメモリに戻さない（最大の安定要因）

#### E. Overlay（写り込み対策の最適化）

* 現状はパイプライン実行中に Overlay を `Hide()` し、最後に `Show()` しています（写り込み回避として妥当）。 
* 推奨：`WDA_EXCLUDEFROMCAPTURE` を OverlayWindow に適用し、“原則写らない”前提に移行。Overlay自体は `WS_EX_TRANSPARENT` 等でクリック透過化済みなので相性も良いです。 

  * 失敗環境のみ、今の Hide/Show フォールバックを維持（最重要）

---

## 2) 「ここまでやると勝ち」要点を現行へ落とす

### 2-1. ROIを“絶対座標”から“正規化座標”へ（最優先の安定化）

* 現状：ROI選択はDIP→Device変換し、`AppSettings.Roi` に **デバイス絶対座標**を保存しています。   
* 問題：DPI/解像度/モニタ構成で破綻しやすい。
* 改修方針：

  * 保存：キャプチャ対象（Window/Monitor/VirtualScreen）の **FrameBounds** に対する 0..1 正規化（x,y,w,h）
  * 復元：キャプチャ時に `FrameBounds` から復元して Clamp → 実ROI
  * 互換：既存 `SerializableRect` を残しつつ `NormalizedRoi` を追加し、移行期間は両対応（ロード時に変換できる範囲で自動移行）

### 2-2. イベント駆動＋バックプレッシャ（体感遅延を増やさない）

* 現状：`PipelineOrchestrator.RunOnceAsync()` は `SemaphoreSlim` で並列抑止しているため、ここを土台に「最新優先」へ拡張しやすいです。 
* 実装方針：

  * Captureはフレーム到着イベントで push
  * OCRがbusyなら “古いフレームは捨てる”（Bounded Channel size=1 など）
  * Overlay更新は最新結果のみ（遅延が蓄積しない）

### 2-3. Provider選択の“学習”

* 追加設定（永続化）：

  * “アプリ識別子 → 最後に成功したProvider”
  * Providerごとの “黒判定連続→クールダウン期限”
* ログ可観測性：`AppLogger` に provider, black_count, ocr_ms, queue_drop を出す（現場で原因特定できる）。 

---

## 3) 失敗しないロードマップ（段階導入・常にロールバック可能）

### Phase 0：差し替え準備

**目的**：既存機能を一切壊さず、Capture差し替えの受け皿を作る

* `ICaptureProvider` と `CapturedFrame` を導入（まずは Bitmap だけでも良い）
* `CaptureManager` の中身を `GdiCaptureProvider` として移植（現行と同一動作） 
  **成功条件**：完全に同じ挙動（キャプチャ領域、ROI、Overlay、OCR）が維持される

### Phase 1：WGC並行実装（CPU bitmap化して現行パイプラインへ投入）

**目的**：WGC導入による環境依存リスクを最小化して“動作確認”だけ先に終わらせる

* `WgcCaptureProvider` を追加（出力は当面 `Bitmap` でOK）
* 失敗時は即 `GdiCaptureProvider` にフォールバック
* 黒判定はまだ簡易で可（例：全黒率だけ）
  **成功条件**：WGC対応環境で GDIと同等以上の成功率、かつ不安定時は自動で戻る

### Phase 2：黒画面/タイムアウト/フォールバック完成（最重要）

**目的**：“落ちない・固まらない・黒でも復帰する”を先に完成

* FrameGateに黒判定（平均＋分散＋連続数）
* Providerクールダウンと自動切替
* 重要：フォールバックをログに残す（後で改善できる） 
  **成功条件**：排他FS/ハードウェアオーバーレイ/黒画面系で復帰する（少なくとも“詰まらない”）

### Phase 3：ROI正規化（安定性の要、ここで勝ちが決まる）

**目的**：DPI/解像度/モニタ構成変更で壊れないROI

* `AppSettings` に `NormalizedRoi`（0..1）を追加し、既存 `Roi` と併存 
* ROI選択UIは、選択結果を “現在のFrameBounds基準で正規化して保存”
* パイプライン側は “正規化があればそれ優先、なければ従来絶対座標”
  **成功条件**：DPI 125%/150%、モニタ増減、解像度変更でROIが維持される

### Phase 4：イベント駆動＋バックプレッシャ（体感品質の仕上げ）

**目的**：OCRが重くても遅延が蓄積しない

* Captureフレーム→Gate→OCR を Channel/Queueで接続し、常に最新フレーム優先
* `PipelineOrchestrator` の `SemaphoreSlim` は維持しつつ “古いフレーム破棄”へ拡張 
  **成功条件**：負荷が上がっても操作感が一定（遅延が伸び続けない）

### Phase 5：GPU前処理（ROIだけCPUへ、GPUファーストの完成）

**目的**：CPUコピー量激減＋安定性と性能の両取り

* WGC出力を `ID3D11Texture2D` として保持
* `GpuPreprocess`：ROIコピー→縮小→（可能なら）グレースケール/二値化
* OCR直前だけ staging `Map` でCPUへ
  **成功条件**：高頻度でも安定、OCR対象データ量が最小化される

### Phase 6：DXGI Desktop Duplication（最後の穴埋め）

**目的**：排他FS等の取りこぼしを減らす

* `DxgiDuplicationProvider` を追加（モニタ単位）
* ActiveWindowモードは「モニタ複製→ウィンドウ矩形でcrop」で代替（WGC不可時の救済）
  **成功条件**：排他FS/黒画面系の成功率がさらに上がる

---

## 4) 実務上の落とし穴チェックリスト（ここを潰すと失敗しにくい）

* **Overlay写り込み**：WDA_EXCLUDEFROMCAPTUREを基本、失敗環境は現行の Hide/Show 維持（既に `PipelineOrchestrator` で実装済み）。 
* **ROI座標系**：正規化移行を最優先（`RoiSelectorWindow` は現状デバイス座標を返すため、保存形式の変更が必要）。 
* **pHash性能**：現状 `GetPixel` ループは高頻度だと重くなり得るため、Phase 4以降は “縮小済みROIに対してのみ” 実行、最終的にGPU側へ寄せる。 
* **OCR入力の最小化**：WinOCRは最終的に `SoftwareBitmap` が必要（現行もBitmap→SoftwareBitmap変換）。GPU導入後も「ROIだけCPUへ」が基本方針。 

---

