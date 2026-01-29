
---

## 1. 全体アーキテクチャ構成

システムを以下の5つの主要モジュールに分割します。

1. **Capture Manager**: `Windows.Graphics.Capture` を制御。
2. **Local Vision (OCR)**: Windows Media OCR (UWP API) を使用。
3. **Cache Logic & Filter**: レベルA/B/Cの判定を行い、APIリクエストを極限まで削減。
4. **Translation Service**: Gemini 3.0 Flash と通信 (Structured Output)。
5. **Overlay Presenter**: WPFの透明Windowで描画。

---

## 2. データフローと処理パイプライン

ホットキー押下から表示までのフローを、キャッシュ判定の順序に従って設計します。

### Step 1: キャプチャとROI判定（レベルBの実装）

* **入力**: 画面全体のバッファ
* **処理**:
1. 事前にユーザーが指定した「会話枠」などのROI（関心領域）を切り出す。
2. **【レベルB：pHashチェック】**
* ROI画像のpHash（Perceptual Hash）を計算。
* 前回のpHashとハミング距離を比較。閾値以下（ほぼ同じ画像）なら、**処理を即終了**（前回のオーバーレイを維持）。
* *ポイント*: アニメーションによる微細なズレを許容するため、MD5ではなくpHash必須。





### Step 2: ローカルOCRと領域分析（レベルCの実装）

* **入力**: ROI画像（または全画面）
* **処理**:
1. Windows Media OCRを実行し、テキストとBounding Box（矩形）を取得。
2. **【レベルC：領域別キャッシュ】**
* 取得した「テキストブロックの座標群」が、前回とほぼ同じ位置にあるか確認。
* 「座標が同じ」かつ「OCRされた生テキストが同じ」場合、そのブロックは処理スキップ（背景が動いていてもテキストが同じなら無視）。





### Step 3: テキスト正規化とキャッシュ照会（レベルAの実装・最重要）

* **入力**: 新規に検出されたOCRテキスト行（リスト）
* **処理**:
1. **正規化 (Normalization)**:
* 記号除去、全角/半角統一、余分な空白削除を行い `SearchKey` を生成。


2. **【レベルA：辞書照会】**
* ローカルDB (SQLite or ConcurrentDictionary) に `SearchKey` + `GlossaryVersion` で問い合わせ。
* ヒットした場合: APIリストには追加せず、キャッシュから訳文を取得。
* ヒットしなかった場合: APIリクエストリスト（`PendingList`）に追加。





### Step 4: Gemini API リクエスト (Structured Output)

* **入力**: `PendingList`（キャッシュになかった未知のテキストのみ）
* **処理**:
* Gemini 3.0 Flash にリクエスト。
* **プロンプト設計**: OCRで誤認識しやすいノイズを考慮し、「翻訳」だけでなく「OCR補正」も期待するが、座標はOCRの結果を正とするため、テキストのマッピングのみを要求する。



**JSON Schema (Structured Output) 案**:

```json
{
  "type": "object",
  "properties": {
    "translations": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "source_text": { "type": "string" },
          "translated_text": { "type": "string" }
        },
        "required": ["source_text", "translated_text"]
      }
    }
  }
}

```

### Step 5: 結果のマージと表示

* **処理**:
1. APIからの戻り値をレベルAキャッシュ（DB）に保存。
2. 「キャッシュから取得した訳文」と「APIから取得した訳文」を、OCRの座標情報に合わせてマージ。
3. WPFのオーバーレイWindow（`Canvas` または `SkiaSharp`）に描画。背景色付きのTextBlockを配置し、元の文字を隠すように被せる。



---

## 3. 各レベルの実装詳細戦略

### レベルA：OCRテキストキャッシュ（永続化層）

これがコスト削減の肝です。

* **Key構造**:
```csharp
string Key = $"{SrcLang}_{DstLang}_{StyleId}_{GlossaryVer}_{NormalizedSourceText}";

```


* **ストレージ**:
* メモリキャッシュ (`MemoryCache`): 直近のヒット用（高速）。
* ディスクキャッシュ (`SQLite` or `LiteDB`): アプリ再起動後も有効にするため。ゲームごとのファイル分割を推奨。


* **工夫**:
* 「はい」「いいえ」「装備する」などの短い単語は即座にキャッシュされるため、プレイすればするほどAPIコールが0に近づきます。



### レベルB：ROI pHash（揺れ対策）

* **UI要件**:
* ユーザーが「ここがメッセージウィンドウ」と矩形選択できるUIが必要。
* 全画面監視だと、キャラの待機モーションだけで「画面更新」とみなされるため、ROI指定は必須。


* **ライブラリ**: `CoenM.ImageHash` 等の軽量ライブラリを使用。

### レベルC：領域別キャッシュ（Vision座標フォールバック）

* **ロジック**:
* OCR結果の `OcrResult.Lines` ループ内で判定。
* `CurrentLine.Text` == `PrevLine.Text` かつ `Rect.Intersects(PrevRect)` 率が高い場合、「同じ物体」とみなす。
* これにより、背景の雲が流れていても、メッセージウィンドウ内の文字が変わらなければAPIを呼ばない挙動を実現。



---

## 4. 開発における技術的ポイント

### WPF + Windows.Graphics.Capture

* **相互運用 (Interop)**: WPFは標準で `GraphicsCapturePicker` を呼べないため、`IInitializeWithWindow` インターフェースを使用して `Window Handle (HWND)` を渡す実装が必要です。
* **Direct3D11**: キャプチャしたフレーム（`Direct3D11CaptureFrame`）をCPUメモリ（`SoftwareBitmap`）に転送し、OCRエンジンに渡すパイプラインの速度が重要です。

### Gemini 3.0 Flash のパラメータ

* **Temperature**: 0.1 ～ 0.3 推奨（創造性は不要、安定した翻訳が必要）。
* **Response Format**: `json_schema` を厳密に定義することで、パースエラーによる再試行（コスト増）を防ぎます。

### 座標の信頼度設計

* 基本方針通り **「OCR座標が絶対」** とします。
* Geminiには座標を渡さず、テキストリストだけを渡します。Geminiがテキストを分割・結合して返してくるとマッピングが崩れるため、プロンプトで**「入力された配列の順序と個数を絶対に変えずに翻訳すること」**と指示するのが重要です。

---

**段階的にリスクを潰しながら完成形へ到達する開発ロードマップ**。

---

## 0. 先に決めるべき非機能要件（失敗回避の要）

最初にこの4点を固定し、以降の実装判断をブレさせないのが重要です。

1. **動作モード**

   * MVPは「ホットキー押下時のみ1回処理（単発）」にする


2. **性能予算（目標値）**

   * キャプチャ→ROI切出→pHash：**～10ms**
   * OCR（ROIのみ）：**～80–150ms（環境差あり）**
   * Gemini：**ネットワーク依存**（ここはキャッシュで極小化）
   * Overlay反映：**～16ms（体感遅延を減らす）**

3. **失敗時の挙動（フォールバック方針）**

   * OCR失敗：前回Overlay維持＋トースト（ログ）
   * Gemini失敗：翻訳欠落のまま表示（原文隠しはしない or “未翻訳”表示）
   * キャプチャ失敗：原因分類（権限・排他フルスクリーン・対象未選択）をUIに出す

4. **サポート範囲（最初の出荷対象）**

   * OS：Windows 11（または Win10 19041+ など明確に固定）
   * 描画：WPF（DPIとマルチモニタ必ずテスト）
   * ゲーム：まずは「ボーダレス/ウィンドウ」前提（排他フルスクリーンは失敗率が上がる）

---

## 1. フェーズ分割ロードマップ（ゲート付き）

「先に難所を通す」順にします。特に **WGC→CPU転送→OCR** が最初の関門です。

### Phase 1（最優先）: キャプチャとOCRの“動く最小系”を完成させる

**目的**：WGCとWindows Media OCRが、WPFデスクトップで安定して動くことを最短で証明。

**実装スコープ**

* Capture Manager

  * `GraphicsCapturePicker` をWPFから起動（HWND渡し）
  * 対象ウィンドウを選択して **1フレーム取得**
  * `Direct3D11CaptureFrame` → CPU（`SoftwareBitmap` 等）へ転送
* Local Vision (OCR)

  * OCRを1回実行し、行テキストとBounding Box取得
* Overlay Presenter（暫定）

  * 透明Topmost WPF Windowで **取得矩形にテキスト枠を描画**（翻訳は不要）
  * クリック透過（マウス操作邪魔しない）

**品質ゲート（Phase 1の合格条件）**

* 30回連続ホットキーで落ちない（例外・リークなし）
* DPI 125% / 150% の環境で、Overlay座標が目視でズレない（許容±数px）
* キャプチャ対象切替ができる（再Pickで再初期化が破綻しない）

**失敗しがちなポイント（先回り）**

* WPFとWinRTのスレッド境界：**UIスレッドでPicker、処理はバックグラウンド**
* D3Dリソース解放漏れ：Frame/Surface/TextureのDispose設計を最初に固める
* DPI座標：OCRのRectとWPF Canvas座標の変換（物理pxとDIPの混同）を早期に潰す

---

### Phase 2: ROI選択UI + レベルB（pHash）で「処理しない」を作る

**目的**：コスト削減の前に、まずCPU負荷を落とす。ROIがないと失敗率が上がるため最優先で入れます。

**実装スコープ**

* ROI選択UI（ユーザーが矩形指定）

  * キャプチャ対象上にガイド表示、または設定画面で座標指定
  * ROIは **対象ウィンドウ座標系** で保存（DPI込み）
* レベルB pHash

  * ROI画像のpHash算出
  * 前回pHashとの距離が閾値以下なら **以降全部スキップ（OCRもしない）**

**品質ゲート**

* 会話が変わらない場面でホットキー連打してもOCRが走らない（ログで確認）
* しきい値調整がUI設定で可能（環境差が出るため）

**実装上のコツ**

* pHashは「ROI縮小＋グレースケール」で高速化（毎回フル解像度でやらない）
* pHash判定は**最初に**置く（OCR前に止めるのが価値）

---

### Phase 3: レベルC（領域別キャッシュ）でOCR後の無駄を止める

**目的**：背景変化に強くする。OCRコストは払ったが翻訳に行かない、を実現。

**実装スコープ**

* OCR結果（行/ブロック）の構造体を独自モデル化

  * `Text`, `Rect`, `Confidence`（取れるなら）
* 前回OCR結果との突合

  * `Text`一致＋RectのIoU（Intersect率）で同一判定
  * 同一判定なら **その行はPendingに入れない**

**品質ゲート**

* 背景アニメありでも文字が同じなら翻訳要求が増えない（統計ログ）
* 誤差（Rect微妙な揺れ）に対して安定して同一判定できる（IoU閾値の設定）

---

### Phase 4: レベルA（最重要）— 正規化＋永続キャッシュ（DB）を入れる

**目的**：Geminiコストを構造的にゼロへ近づける。ここで初めて「運用に耐える」段階に入ります。

**実装スコープ**

* Normalization（SearchKey生成）

  * 全角半角、空白圧縮、記号除去、よくあるOCR誤り補正（最低限）
  * ここはユニットテストを厚くする（後から壊れやすい）
* キャッシュ層二段

  * MemoryCache（LRU相当でも良い）
  * SQLite（またはLiteDB）
* スキーマ設計

  * Key（`Src/Dst/StyleId/GlossaryVer/NormalizedText`）
  * Value（translated_text, created_at, hit_count など）
  * インデックス：Keyにユニーク制約

**品質ゲート**

* アプリ再起動後もキャッシュヒットする
* 同じ文が繰り返し出るゲームで、数十分プレイ後のAPI呼び出しが大きく減る（計測）

---

### Phase 5: Gemini統合（Structured Output）— “壊れない通信”を作る

**目的**：失敗時の再試行がコスト増になりやすいので、**最初から堅牢**に作る。

**実装スコープ**

* PendingListのバッチ化（上限N行）
* Structured Output（JSON Schema）強制
* 重要：**入出力の行数・順序を変えない** 制約をプロンプトで固定
* リトライ戦略

  * 429/5xxは指数バックオフ＋最大回数
  * 4xx（スキーマ違反等）は即失敗扱い（再試行は悪化しがち）
* 例外分類ログ（後で原因究明できるように）

**品質ゲート**

* JSONパース失敗が“ほぼゼロ”になる（スキーマと温度の調整）
* Gemini失敗でもアプリが落ちない・UIが固まらない（必須）

---

### Phase 6: Overlay品質を仕上げる（視認性・ズレ・負荷）

**目的**：機能が揃っても、Overlayがズレる・チラつく・重いと実用不可。

**実装スコープ**

* 描画方式の確定（WPF Canvas / SkiaSharp）
* テキスト背景・角丸・行間・最大幅処理
* クリック透過、Alt+Tab、最前面維持、キャプチャ対象切替時の追従
* DPI/マルチモニタ/ウィンドウ移動・リサイズ追従

**品質ゲート**

* ウィンドウ移動/解像度変更してもOverlayが追従
* 表示がちらつかない（前回維持と更新の切り替えが自然）

---

### Phase 7: 運用機能（設定・鍵管理・ログ・更新）

**目的**：現場投入で壊れる要因（設定壊れ・鍵漏えい・ログ不足）を潰す。

**実装スコープ**

* 設定画面（ROI、閾値、言語、スタイル、GlossaryVer）
* APIキー保存（DPAPI等でローカル保護）
* ログ（処理時間、ヒット率、API回数、例外）
* パッケージング（MSIX等）と自動更新方針（任意）

---

## 2. CODEX 5.2に書かせるための「分割指示」テンプレート

失敗の典型は「一括で全部書かせて破綻」です。**モジュール単位＋受け入れ条件付き**で生成させます。

### 推奨の生成単位（この順で）

1. `CaptureManager`（Picker→Frame→Bitmapまで）
2. `OcrEngine`（Bitmap→OcrResultModel）
3. `OverlayWindow`（Rect→描画）
4. `RoiSelector`（UI＋保存形式）
5. `PhashService`（ROI→hash→distance）
6. `OcrDiffService`（前回OCRとの差分）
7. `NormalizationService`（SearchKey生成＋テスト）
8. `CacheRepository`（Memory＋SQLite）
9. `GeminiClient`（Schema固定＋リトライ）
10. `PipelineOrchestrator`（ホットキーから全体制御）

### CODEXに必ず付ける制約（例）

* 「例外を握りつぶさず、分類してログに出す」
* 「Dispose/解放責務を明確に（IDisposable設計）」
* 「CancellationToken対応（ホットキー連打で前処理キャンセル）」
* 「UIスレッドブロック禁止（ConfigureAwait等）」
* 「DPI変換関数を1か所に集約」

---

## 3. テスト戦略（“壊れない”の中核）

自動テストが入れにくい領域（WGC/OCR/Overlay）が多いので、**テストを二層に分けます**。

### A. ユニットテストで固める領域（最重要）

* Normalization（文字種・記号・空白・全半角）
* Key生成とバージョニング（GlossaryVer含む）
* pHash距離の閾値挙動（疑似画像で）
* OCR差分（Rect IoU判定、安定性）

### B. 手動＋自動混在の統合テスト（テストハーネスを用意）

* 「テスト用固定画像」を読み込み、OCR→Overlayまで流せるモード
* 「WGC実キャプチャ」はスモークテスト（環境依存が強い）

### C. 計測（これがないと最適化できない）

* 各ステップ時間（Capture/ROI/pHash/OCR/Normalize/Cache/Gemini/Draw）
* キャッシュヒット率（A/B/Cそれぞれ）
* API呼び出し回数/セッション

---

## 4. リスク登録簿（よくある破綻ポイントと対策）

1. **排他フルスクリーンがキャプチャできない**

   * 対策：ボーダレス推奨、検知してユーザーに明示

2. **DPI/マルチモニタでRectがズレる**

   * 対策：座標系（物理px/DIP）を最初に定義し、変換関数を統一

3. **OCRが遅い／精度が不安定**

   * 対策：ROI必須、文字サイズが小さい場合の拡大（スケーリング）を検討、Phase 1で実測して限界を把握

4. **Geminiの返答がスキーマ通りにならない**

   * 対策：Response Format厳格＋温度低＋入力配列の不変を強制。パース失敗は即失敗（再試行で悪化しやすい）

5. **ホットキー連打で競合・多重実行**

   * 対策：単一実行制御（SemaphoreSlim）＋CancellationTokenで前回をキャンセル

---

## 5. 最終的な推奨実装順（要点だけ抜粋）

* **最初に通すべき山**：WGC→CPU→OCR→Overlay（Phase 1）
* **次に“止める”**：ROI＋pHash（Phase 2）
* **次に“翻訳へ行かない”**：領域差分（Phase 3）
* **次に“永続でゼロに近づける”**：Normalization＋SQLite（Phase 4）
* **最後に“外部要因を内側で吸収する”**：Gemini堅牢化（Phase 5）

---

