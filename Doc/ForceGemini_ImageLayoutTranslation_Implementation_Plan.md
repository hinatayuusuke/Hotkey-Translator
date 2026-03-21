# ForceGemini Image Layout Translation Implementation Plan

## 1. 概要（1–3行）
`ForceGeminiStrict` を「Gemini を翻訳プロバイダ固定にする hotkey」から、「OCR をスキップして ROI 画像を Gemini に直接渡し、できるだけ元レイアウトを保った翻訳文を返す hotkey」へ変更する。  
v1 では座標抽出や bbox JSON は要求せず、ROI 全体に対する単一ブロック翻訳として overlay へ表示する。  
既存の通常 OCR パイプラインは維持し、`ForceGeminiStrict` 実行時だけ専用分岐に入る。

## 2. ゴール / 非ゴール
### ゴール
- `ForceGeminiStrict` 実行時は OCR / line merge / OCR diff を通さず、ROI 画像を Gemini に直接送る。
- Gemini には「できるだけ元の行分割・空行・段落を維持した翻訳」を要求する。
- overlay は ROI 全体を覆う単一ブロックとして表示し、既存通常モードへの影響を避ける。

### 非ゴール
- Gemini に文字座標、bbox、reading order JSON を要求すること。
- 通常 OCR モードを image-to-Gemini へ置き換えること。
- 既存 translation cache や OCR diff をこのモードへ無理に統合すること。
- 翻訳結果を元の reading unit 単位へ再分配すること。

## 3. 前提・仮定
- 現状の `ForceGeminiStrict` は OCR 後の translation provider を Gemini 固定にするだけで、OCR 自体は通常どおり走る。
- `GeminiClient` は現在 text array を入力として `application/json` schema 応答を受ける text-only 実装である。
- 座標を要求しない場合、既存の reading unit 単位 overlay には直接流せない。
- そのため v1 は ROI 全体を 1 つの synthetic block として扱うのが最も安全である。

## 4. 現状整理
- hotkey 起点は `Services/Application/HotkeyCommandController.cs` の `HandleForceGeminiStrictHotkeyAsync()`。
- pipeline 本体は `Services/PipelineOrchestrator.cs` の `RunOnceAsync(...)`。
- 現在の `ForceRunOptions.ForceGeminiStrict` は `TranslationFallbackService` の provider 選択でのみ使われている。
- `GeminiClient.TranslateAsync(...)` は text list を prompt 化して JSON schema 応答を受ける。
- `OverlayStage` は通常は `ReadingUnit` ごとの overlay item を作るが、`EnableFixedRoiOverlay` 時は単一ブロック表示も持っている。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `Services/PipelineOrchestrator.cs`
  - `ForceGeminiStrict` 専用の image translation 分岐を追加する。
- `Services/GeminiClient.cs`
  - ROI 画像を Gemini に送る image translation API を追加する。
- `Services/Orchestration/Stages/OverlayStage.cs`
  - 既存の単一ブロック表示ロジックを活かしつつ、synthetic unit 1 件でも表示できるように使う。

### データフロー / シーケンス
1. hotkey で `ForceGeminiStrict` 実行。
2. `PipelineOrchestrator` は通常どおり frame capture と ROI crop までは行う。
3. `ForceGeminiStrict` が有効な場合、OCR stage をスキップする。
4. `GeminiClient` に ROI bitmap を直接渡し、レイアウト保持翻訳 prompt で問い合わせる。
5. 返却テキストを ROI 全体を覆う single synthetic reading unit に詰める。
6. overlay は単一 block として表示する。

### 既存パターンへの整合
- 通常 OCR モードの挙動は変えない。
- `ForceGeminiStrict` だけを pipeline の専用実行モードとして扱う。
- translation provider fallback ではなく、OCR 代替付きの image mode として責務を分ける。

## 6. インターフェース設計
### `ForceRunOptions`
- `ForceGeminiStrict` は継続利用するが、意味を「Gemini-only text translation」から「Gemini image-layout mode」へ拡張する。

### `GeminiClient`
- 新規メソッド案:
```csharp
public async Task<string?> TranslateImagePreservingLayoutAsync(
    Bitmap roiBitmap,
    AppSettings settings,
    CancellationToken cancellationToken)
```

### 入力
- ROI 画像を PNG へエンコードして Gemini `generateContent` に `inline_data` で送る。
- prompt は平文応答前提で、以下を必須にする。
  - 見えている文字だけを翻訳
  - 説明、注釈、JSON、Markdown を出さない
  - 行分割、空行、段落、箇条書きをできるだけ維持
  - 判読不能箇所は無理に補わない

### 出力
- `text/plain` の翻訳全文
- 空文字または失敗時は `null`

### エラー / バリデーション
- Gemini disabled / API key missing / HTTP error / empty response は fail fast で `null`
- 既存と同様に raw response dump は残せるようにする

## 7. 実装手順（ステップ分割）
### Step 1
- `GeminiClient` に image translation 用 API を追加する。
- text translation 用 prompt / schema と責務を分離する。

### Step 2
- `PipelineOrchestrator` で ROI crop 後に `ForceGeminiStrict` 専用分岐を追加する。
- OCR stage、diff stage、translate stage を通さない。

### Step 3
- ROI 全体を覆う synthetic `ReadingUnit` または同等の overlay input を 1 件生成する。
- 翻訳結果全文をその block に出す。

### Step 4
- 失敗時は既存の last overlay 維持方針に合わせる。
- ログに `force_gemini_image` 経路の成功 / 失敗を出す。

## 8. 非機能要件チェック
### 性能
- OCR を丸ごと skip するため CPU OCR コストは減る。
- 一方で Gemini 往復待ちが支配的になるため、UI は既存 busy overlay を使う前提にする。

### セキュリティ

### 可観測性
- `stage=force_gemini_image` の begin / success / fail ログを追加する。
- raw response dump は既存 Gemini dump の仕組みを再利用する。

### 互換性
- 通常 OCR モードと translation provider 優先順には影響しない。
- `ForceGeminiStrict` の意味が変わるため、利用者向けには hotkey 説明の更新が必要。

## 9. リスクと緩和策
- Risk: Gemini が余計な説明文や要約を返し、overlay にそのまま出る可能性がある。
- Mitigation: prompt を強く制約し、plain text only・説明禁止を明示する。

- Risk: 座標なしなので元の吹き出し単位や行位置は維持できない。
- Mitigation: v1 は ROI 全体 single block と割り切り、「大まかなレイアウト維持」にスコープを限定する。

## 10. 影響範囲
- `Services/Application/HotkeyCommandController.cs`
- `Services/PipelineOrchestrator.cs`
- `Services/GeminiClient.cs`
- 必要なら `Services/Orchestration/Stages/OverlayStage.cs`
- Doc
  - この計画書

## 11. Definition of Done
- `ForceGeminiStrict` 実行時に OCR が走らず、ROI 画像が Gemini に直接送られる。
- Gemini 応答は座標 JSON ではなく plain text のレイアウト保持翻訳として扱われる。
- overlay は ROI 全体の single block として翻訳全文を表示できる。
- 通常 OCR モードと既存 translation provider fallback の挙動は変わらない。
