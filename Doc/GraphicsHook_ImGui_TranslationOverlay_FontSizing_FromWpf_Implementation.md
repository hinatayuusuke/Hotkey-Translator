# GraphicsHook(ImGui) 翻訳オーバーレイ: WPFのフォントサイズ算出を v2(fontPx) に流用する実装案

対象: DX11 hook overlay (v2 TextBlock)  
目的: 「枠(Rect)に収まるようにフォントサイズを動的に決める」ロジックを、既存のWPFオーバーレイの算出結果（=既に実績のあるFitting/安定化）を基準に、Hook(ImGui)側へ反映する。

関連:
- WPF overlay 実装: `UI/OverlayWindow.xaml.cs`
- v2 writer 実装: `Services/PipelineOrchestrator.cs` → `Dx11HookClientService.TryWriteOverlayV2(...)`
- Native renderer: `Native/HookAgentDx11/Dx11PresentHook.cpp`

---

## 1. 結論（推奨方針）
「WPFで使っているフォントフィット（FitMaxFont/Fits）を *そのまま* 参照できる形に分離し、WPF overlay と Hook overlay の両方が同じエンジンから `fontSizeDip` を得る」方式を推奨する。

理由:
- WPF側は既に `FormattedText` による収まり判定、安定化（量子化/ヒステリシス/キャッシュ）がある。
- Hook側は `fontPx` が来れば描画できる（Native側はフォントサイズを複数段階で選べる）。
- 「WPF overlay が見えない/フルスクリーン下に潜る」状況でも、計算エンジンは使える（UI表示とは切り離す）。

---

## 2. ゴール / 非ゴール
### ゴール
- WPF overlay と Hook overlay でフォントサイズの“方針”が一致し、枠に合わせて文字が拡大/縮小する。
- 既存のWPF側の安定化（ジッタ抑制、ヒステリシス）をHook側にも反映できる。
- 実装が段階導入でき、問題があれば `HT_HOOK_OVL_TEXT_DRAWLIST=1` 等のフォールバックに退避できる。

### 非ゴール
- WPFとImGuiで“完全に同じ見た目”の文字組（行間、禁則、字形差）を達成すること。
- 文字単位の高度なレイアウト（Harfbuzz shaping 等）。

---

## 3. 現状整理（重要ポイント）
### 3.1 WPF overlay のフォントサイズ決定
`UI/OverlayWindow.xaml.cs` の `UpdateItems(...)` 内で以下を行っている:
- `ResolveOverlayItemLayout(...)`: small-box readability boost による矩形拡張 + base font の調整
- `ResolveFontSize(...)`:
  - `FitMaxFont(...)`（二分探索） + `Fits(...)`（`FormattedText`計測）
  - stabilizer: `QuantizeLength`, cache key, hysteresis
- 得られた `fontSize`（DIP）を `TextBlock.FontSize` に設定して描画

### 3.2 Hook overlay の現状
- v2 の `TextBlockV2.fontPx` は、C#側で固定値で送っている（枠が大きくても文字が大きくならない）。
- Native側では:
  - per-window 描画（折り返し/クリップ/NoInputs）を維持
  - `SetWindowFontScale` は廃止し、複数段階のフォントを事前ロードして `PushFont/PopFont` で切替
  - `fontPx` は「要求値」として扱い、近い段階に丸めて描画する
  - 現在のフォント段階は 12個で、範囲は `[14..72]`（両端含む、線形）

### 3.3 重要: 単位系の違い（DIP / Screen px / Canvas px）
- WPF の `Rect` と `FormattedText` の `MaxTextWidth/Height` は基本的に DIP（device-independent pixel）で扱うのが自然。
- OCR/キャプチャ側の `Rect` や `frame.Bounds` は screen device px（モニタの実ピクセル座標）で扱うことが多い。
- Hook v2 の `TextBlockV2.(x,y,w,h)` と `fontPx` は Canvas/backbuffer px（Present内で描く座標）で安定させたい。

このため、Hook writer（C#）は次をやるのが最も事故が少ない:
1. `Rect screenPx`（OCR由来）→ `Rect dip` に落とす（フォントフィット用）
2. `fontSizeDip`（fit結果）→ `fontPx`（Canvas px）に上げる（Hookへ送る）

---

## 4. 方式比較（採用理由の明確化）
### Option A（推奨）: フォントフィットを共有エンジン化
- WPFもHookも同じ `OverlayFontFitter` を使う
- Hookは `fontSizeDip` を `fontPx`（canvas px）に変換して送る
  - “同じアルゴリズム”を共有でき、差分が減る

### Option B: WPFの「最終的に採用したFontSize」を後追いで取得してHookへ反映
- 実装が複雑になりがち（UIスレッド更新の完了待ち、同一アイテム同定、非表示時の挙動）
- 設計として “表示” と “計算” が強く結合しやすい

本ドキュメントは Option A を詳細化する。

---

## 5. 提案アーキテクチャ
### 5.1 新コンポーネント: OverlayFontFitter（C#）
目的: WPF overlay が既に持つロジックを「計算専用」に切り出し、WPF/Hook 両方で利用する。

配置案:
- `Services/Overlay/OverlayFontFitter.cs`（内部クラス、UI依存は `FormattedText` 最小）
  - もしくは `UI/OverlayFontFitter.cs`（UI層に置く）

入力（例）:
- `text: string`
- `availableWidthDip: double`
- `availableHeightDip: double`
- `baseFontSizeDip: double`
- `settings`: 安定化有無、量子化ステップ、ヒステリシス閾値など
- `typeface/brush/pixelsPerDip`: `FormattedText` 用

出力:
- `fontSizeDip: double`

状態:
- 既存の `_fontSizeCache` 相当（キャッシュ/ヒステリシス）を保持する（インスタンスを使い回す）


### 5.2 新データ: OverlayRenderMetrics（C#）
Hookへ渡すために最低限必要:
- `Rect rectScreen`（元のOCR由来Rect、screen device px）
- `double fontSizeDip`（WPFの計算結果）

将来拡張:
- small-box readability boost の拡張rectもHookへ反映したい場合:
  - `Rect rectDipExpanded` を保持 → `DpiHelper.DipRectToDevice(...)` で screen px に戻して使う

---

## 6. 変換式（screen px ⇔ DIP ⇔ canvas px）
Hook v2 は canvas/backbuffer の px を期待するため、WPFで求めた `fontSizeDip` を `fontPx` に変換する。
同時に、フォントフィットは DIP で行う方が扱いやすいので、screen px から DIP への変換も必要になる。

### 6.1 screen px → DIP（fit入力の作り方）
前提:
- OCR の `Rect` は screen device px
- `dpiScaleX/dpiScaleY` が分かる（WPF visual から取るか、ターゲット hwnd から取る）

変換:
- `dipX = pxX / dpiScaleX`
- `dipY = pxY / dpiScaleY`
- `dipW = pxW / dpiScaleX`
- `dipH = pxH / dpiScaleY`

基本:
- `screenPx = dip * DpiScaleY`
  - `DpiScaleY = dpi.DpiScaleY`（WPFのDPI: `VisualTreeHelper.GetDpi(visual)`）

Hook canvas への変換:
- `scaleY = canvasH / frameBounds.Height`
  - `frameBounds` は screen device px である前提（現行の `TryBuildHookCanvasRect` と同じ）
- `fontPx = screenPx * scaleY`

メモ:
- 画像例のように `canvas == bb == frameBounds` なら概ね `scaleY=1` で `fontPx ≒ dip * DpiScaleY`

### 6.2 DIP → canvas px（v2 fontPxの作り方）
まとめると:
- `fontPx = fontSizeDip * dpiScaleY * (canvasH / frameBounds.Height)`

`frameBounds.Height` が 0 の場合は `scaleY=1` にフォールバックする。

### 6.3 DPI の取得（現実的な選択肢）

Option 2（推奨）: ターゲット hwnd の DPI を Win32 から取る
- `GetDpiForWindow(hwnd)`（Win10以降）を使い、`dpiScale = dpi / 96.0`
- WPFの表示有無に依存しない

どちらでも良いが、Hook v2 writer を「WPF overlay の表示/非表示から独立」させたいなら Option 2 が堅い。

---

## 7. 実装ステップ（詳細）
### Step 1: OverlayFontFitter を切り出す（WPF overlay の見た目を変えない）
`UI/OverlayWindow.xaml.cs` から以下のメソッド群を抽出し、`OverlayFontFitter` に移す:
- `ResolveFontSize(...)`
- `Fits(...)`（`FormattedText`生成）
- `FitMaxFont(...)`
- `QuantizeLength(...)`
- （必要なら）キャッシュキー生成も抽出

注意点:
- `FormattedText` は `pixelsPerDip` と `Typeface` が必要。
- 既存の WPF overlay の見た目を変えないよう、定数（Min/Max/Iterations/ステップ）を揃える。

提案API例（最小）:
- `double ResolveFontSizeDip(string text, double availableWidthDip, double availableHeightDip, string cacheKey, double baseFontSizeDip, OverlayFontFitterOptions opt)`

### Step 2: WPF overlay を OverlayFontFitter 経由に変更
`UI/OverlayWindow.xaml.cs` の `UpdateItems(...)` 内:
- 既存の `ResolveFontSize(...)` 呼び出しを `OverlayFontFitter.Resolve(...)` に差し替える
- `_fontSizeCache` を `OverlayFontFitter` 内へ移す（`OverlayWindow` 側の重複状態を解消）

### Step 3: Hook v2 writer で同じフォントサイズを使う
`Services/PipelineOrchestrator.cs` の `TryUpdateDx11HookOverlayV2(...)` で:
- `fontPx` 固定値を廃止
- `OverlayFontFitter` を使って `fontSizeDip` を計算し、上記変換式で `fontPx` を算出して送る

実装の現実的な置き場:
- `FormattedText` は WPF のテキストエンジンなので、実行スレッドは STA であることが安全（UIスレッド or 専用STAワーカー）。
- 推奨案（安定志向）: `OverlayFontFitterWorker` を用意し、専用 STA スレッドで `FormattedText` を実行する
  - UIの表示/非表示に依存しない
  - UIスレッドの負荷を増やしにくい

提案コンポーネント例:
- `Services/Overlay/OverlayFontFitterWorker.cs`
  - `Task<double> ResolveFontSizeDipAsync(FontFitRequest req, CancellationToken ct)`
  - 内部で STA thread + `Dispatcher` を持ち、要求を順次処理する

Hook v2 writer からの呼び出し例（概念）:
1. `Rect itemRectScreenPx` を `availableWidthDip/HeightDip` に変換
2. `fontSizeDip = await fitter.ResolveFontSizeDipAsync(...)`
3. `fontPx = fontSizeDip * dpiScaleY * (canvasH / frameBounds.Height)` を作り `TextBlockV2.fontPx` に設定
4. 既存の `TryWriteOverlayV2` を呼ぶ

### Step 4: small-box readability boost の反映（任意、後回し可）
WPF側で矩形拡張（`ResolveOverlayItemLayout`）が効いている場合、Hook側も同じRectに寄せたい。
- `OverlayFontFitter` とは別に `OverlayRectBoostEngine` を抽出し、rectも計算結果として返す。
- Hook v2 の `TextBlockV2` は rect を受け取れるので、そのrectをそのまま送る。

### Step 5: デバッグ/検証
Native側（既存）:
- `HT_HOOK_OVL_DEBUG=1` で `block0 font desired/selected` が期待通り変わるか確認

期待する観測:
- 枠が大きい時に `desired` が上がり、`selected` が 14..72 の段階に寄る
- `HT_HOOK_OVL_TEXT_DRAWLIST=1` でも per-window でも、“fontPxが変動する”ことを確認

---

## 8. リスクと対策
- Risk: `FormattedText` 計算が UIスレッド負荷になる
  - Mitigation: OCR/翻訳更新頻度（5〜15Hz程度）でのみ実行。キャッシュ/ヒステリシスを維持。
- Risk: DIP→canvas 変換の前提（frameBoundsがscreen px）違いでサイズがズレる
  - Mitigation: `HT_HOOK_OVL_DEBUG` に `canvas/frameBounds` のスケール診断を追加しやすい設計にする。
- Risk: WPFとImGuiのメトリクス差で「WPFで収まるがImGuiで溢れる」可能性
  - Mitigation: native側は clip を必ず行う（現状 `TextBlockV2` の inner rect で clip 可能）。必要なら `fontPx` に安全係数（例: 0.95）を掛ける。

---

## 9. Definition of Done
- Hook overlay の `fontPx` が枠サイズに応じて変わる（固定値ではない）。
- `HT_HOOK_OVL_DEBUG=1` の `desired/selected` が枠とテキスト量に応じて変動する。
- WPF overlay と Hook overlay の「文字の大きさの傾向」が一致する（完全一致は不要）。
