# VisionLLM Previous-Source Context Assist Plan

## 1. 概要
短い会話ウィンドウ向けに、VisionLLM translation path 専用で、前回の原文 1 item を先頭に追加して同時翻訳させる。JSON 解析後は先頭 item の翻訳結果を捨て、今回 item の翻訳だけを利用する。

## 2. ゴール / 非ゴール
### ゴール
- VisionLLM translation path の短文会話で、前後文脈不足による主語・口調・省略補完のブレを減らす。
- 既存の JSON 構造化出力フォーマットを大きく変えずに導入する。
- 通常の `LlamaGrpcTranslationProvider`、DeepL、Gemini には影響を与えない。

### 非ゴール
- 前回訳文の利用。
- 複数ターン分の長い会話履歴の注入。
- 通常 Llama translation path への同機能の横展開。
- JSON parser rescue の全面見直し。

## 3. 前提・仮定
- 対象は VisionLLM translation path のみ。
- 現在 item が短文で、前回原文も短文のときだけ有効にする。
- 先頭に追加した前回原文の訳は、本番結果としては使用しない。
- VisionLLM translation path が JSON 構造化出力を使う前提でも、返却 item 数の検証で安全側に倒す。
- 通常の Llama gRPC translation path には適用しない。

## 4. 現状整理
- `TranslateStage` は今回の pending item 群を作り、provider へそのまま渡している。
- 通常の `LlamaGrpcTranslationProvider` は `texts` をそのまま gRPC へ送っている。
- VisionLLM translation path は JSON 構造化出力で item 配列を返す実装が前提で、parser rescue もそちらに寄っている。
- `_lastTranslations` はあるが、翻訳コンテキストとしては使っていない。
- そのため、前回文脈を使うなら VisionLLM translation path 側で prepend/strip を完結させるのが最も安全である。

## 5. 提案アーキテクチャ
### 5.1 基本方針
- VisionLLM translation request の item 群先頭に `previousSourceText` を 1 件だけ prepend する。
- VisionLLM server / engine は「N+1件の翻訳要求」として通常どおり処理する。
- 返却時に index 0 の結果を捨て、index 1..N を今回 item へ対応付ける。

### 5.2 なぜ前回原文だけか
- 前回訳文を入れると誤訳が次ターンへ伝播しやすい。
- 原文だけなら、モデルは同一翻訳タスクの継続として扱いやすい。
- prompt を複雑化せずに、少量の文脈だけ足せる。
- JSON schema も item 数を 1 つ増やすだけで済む。

### 5.3 適用条件
- translation provider / path が VisionLLM
- 現在 item が短文
- 前回原文が存在し、かつ短文
- scene 切替などで文脈が切れたと判断される場合は使わない
- item 数ずれや parser error が出た run では fail fast で context assist を無効扱いにする

## 6. インターフェース設計
### 6.1 AppSettings
最小追加候補:
- `EnableVisionLlmPreviousSourceContextAssist` (bool, default false)
- `VisionLlmPreviousSourceContextMaxChars` (int, default 48 or 64)
- `VisionLlmCurrentSourceContextMaxChars` (int, default 48 or 64)

### 6.2 TranslateStage
- 今回 pending item を決める現行処理はそのまま。
- ただし VisionLLM translation path を使う時だけ、context あり/なしを認識できるようにする。
- cache key を VisionLLM context-aware にできるようにする。

### 6.3 VisionLLM translation path
- VisionLLM 専用の translation provider / request builder の内部だけで `previous + currentTexts` の送信配列を作る。
- 返却時は先頭結果を無視し、残りを currentTexts にマップする。
- count mismatch が出た場合は fail fast でその run の context assist を無効にし、通常 path へ落とす。

### 6.4 Context state
新規サービスまたは小さな state holder を追加:
- `LastSourceText`
- `LastSourceLanguage`
- 必要なら `LastUpdatedAt`

用途:
- 直前 run の最後の原文 1 件を保持
- scene change / OCR engine change / provider change でリセット可能にする

## 7. 実装手順
### Step 1: 設定追加
- `AppSettings` に VisionLLM 専用 feature flag と max chars を追加
- 初期値は `false`

### Step 2: context state holder 追加
- 直前の原文 1 件だけ保持する小さなクラスを追加
- pipeline 成功時に最後の reading unit 原文で更新する
- scene 切替や明示条件でクリアできるようにする

### Step 3: VisionLLM translation path で prepend/strip 実装
- VisionLLM translation request を組み立てる箇所に context 付与ロジックを追加
- 適用条件を満たすときだけ `textsWithContext = [previousSource] + texts`
- response は `translations[1..]` だけ採用
- result count が 1 足りない/ずれる場合は fail fast で通常 path に戻すか空扱い

### Step 4: cache key 連動
- context ありの場合だけ、cache key に `previousSource` の normalized digest を含める
- これをやらないと、同じ現文でも前文違いの訳が cache 共有されてしまう
- 通常 Llama / DeepL / Gemini の cache key 仕様は変えない

### Step 5: diagnostics
- VisionLLM translation request 実行時に
  - `context_assist=on/off`
  - `context_chars`
  - `current_count`
  をログへ出す
- parser error / count mismatch 時も context assist 有無を残す

## 8. 非機能要件チェック
### 性能
- 追加するのは最大 1 item だけなので負荷増は小さい。
- 小型モデルでも prompt 爆発を起こしにくい。

### 可観測性
- request ログに context assist の有無を残す。
- parser failure / count mismatch 時に context assist の影響を切り分けやすくする。

### 互換性
- feature flag OFF では完全に現行どおり。
- 通常 Llama translation path、DeepL、Gemini には影響しない。

### 運用
- VisionLLM translation path の短い会話専用機能として扱う。
- 長文 UI / 説明文では基本効かせない。

## 9. リスクと緩和策
- Risk: 前回原文が別シーンの文で、誤った文脈誘導になる。
- Mitigation: scene 切替や長文検出で context を無効化/クリアする。

- Risk: JSON item 数がずれる。
- Mitigation: 先頭 1 件追加を前提に count を検証し、ずれた場合は fail fast でその run の context assist を無効扱いにする。

- Risk: cache 汚染。
- Mitigation: context あり時は VisionLLM translation path の cache key に前回原文 digest を含める。

## 10. 影響範囲
- `Models/AppSettings.cs`
- `Services/Orchestration/Stages/TranslateStage.cs`
- VisionLLM translation request builder / provider 実装
- 必要なら新規 `Services/TranslationContextState.cs`
- `OcrServiceVisionLlm/vision_llama_engine.py` または対応 server diagnostics

## 11. Definition of Done
- feature flag OFF で現行動作が変わらない
- VisionLLM translation path で context assist ON のときだけ前回原文が先頭 item として送られる
- 返却結果は今回 item 分だけ使われ、前回原文の訳は破棄される
- cache key が context あり/なしを区別する
- 通常 Llama translation path、DeepL、Gemini は影響を受けない
- count mismatch / parser error 時に context assist が fail fast で切られる
