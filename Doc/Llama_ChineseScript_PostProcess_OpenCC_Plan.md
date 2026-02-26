# Llama 翻訳後処理: 簡体→繁体（OpenCC）実装案

## 1. 概要（1-3行）
軽量モデルが中国語翻訳で簡体字を出力しやすい問題に対し、`TranslationServiceLlama` 内で翻訳後に `OpenCC` を適用して繁体字へ正規化する。  
適用は `target_lang` が繁体系（`zh-Hant/zh-TW/zh-HK`）の場合のみ有効化し、C# 側の翻訳パイプラインには手を入れない。  
初期は安全重視で「文字体系変換のみ」を行い、誤訳修正は非ゴールとする。

## 2. ゴール / 非ゴール
### ゴール
- 繁体ターゲット時に、Llama翻訳結果を安定して繁体字へ変換する。
- 変換方式を `zh-Hant/zh-TW/zh-HK` で切り替える（`s2t/s2twp/s2hk`）。
- 既存の C# 側フォールバック・キャッシュ・UI を壊さず導入する。

### 非ゴール
- 翻訳意味の誤り（誤訳）そのものの修正。
- Llama以外（DeepL/Gemini/CTranslate2）への同時適用。
- 初期段階での大規模辞書整備（固有名詞補正は最小運用）。

## 3. 前提・仮定
- `TranslationServiceLlama/server.py` は `target_lang` を受け取り、`llama_engine.py` 内で翻訳結果を返している。
- C# 側は provider 返却文字列をそのままキャッシュ/表示に使用するため、Llama内で変換すれば全経路に反映される。
- OpenCC は Pythonライブラリ（`opencc-python-reimplemented`）を採用し、インプロセス変換で実装する。
- 変換は「簡体/繁体の字形統一」が目的であり、品質評価はA/Bで確認する。

## 4. 現状整理
- Llamaの小型モデルでは繁体指定でも簡体出力が混在する。
- 現行の `TranslationServiceLlama` は翻訳結果をそのまま gRPC 応答へ返している。
- 中国語言語ラベル解決は修正済みだが、モデル限界による簡体寄り出力は残る。
- 後処理を C# 共通ステージに置くと他プロバイダへ不要な影響が出るため、Llama内完結が適切。

## 5. 提案アーキテクチャ
### コンポーネント構成
- 新規（Python）: `TranslationServiceLlama/chinese_script_postprocess.py`
  - `convert_for_target(text: str, target_lang: str) -> str`
  - `resolve_mode(target_lang: str) -> Optional[str]`
- 既存更新（Python）:
  - `TranslationServiceLlama/llama_engine.py`
    - `LlamaTranslator` の出力返却直前で後処理を適用

### データフロー / シーケンス
1. `LlamaTranslator.translate()` が翻訳結果（配列）を生成。
2. 各要素に対して `target_lang` を見て OpenCC 変換要否を判定。
3. 繁体系ターゲットなら `s2t/s2twp/s2hk` で変換。
4. 変換後文字列を gRPC 応答として C# 側へ返す。
5. C# 側は既存どおりキャッシュ保存・オーバーレイ表示を実施。

### 既存パターンへの整合
- Llama固有課題を Llamaサービス内で閉じる（責務分離）。
- 変換失敗時は原文訳を維持し、翻訳処理全体を落とさない。

## 6. インターフェース設計
### Python API / 関数
- `resolve_target_variant(target_lang: str) -> Literal["hant","tw","hk"] | None`
- `convert_chinese_script(text: str, variant: str) -> str`
- `postprocess_translation(text: str, target_lang: str) -> str`

### モード対応
- `zh-Hant` -> `s2t`
- `zh-TW` -> `s2twp`
- `zh-HK` -> `s2hk`
- その他 -> 変換なし

### エラー/バリデーション
- 入力空文字はそのまま返す。
- OpenCC例外時は原文を返し、`warning` ログのみ。
- 変換結果が空になった場合は原文を採用（防御）。

## 7. 実装手順（ステップ分割）
### Step 1: 後処理モジュール追加
- `chinese_script_postprocess.py` を新規作成。
- `target_lang` 解析とモード解決ロジックを実装。

### Step 2: OpenCCバックエンド接続
- 方式A（推奨）: Pythonライブラリでインプロセス変換。
- `pyproject.toml` に OpenCC 依存を追加し、`uv sync` で環境を固定化する。


### Step 3: Llama出力へ統合
- `llama_engine.py` の翻訳出力返却直前に後処理を適用。
- 単一翻訳とバッチ翻訳の両経路に同じ後処理を適用。

### Step 4: 設定トグル（任意）
- 初期は常時ONでも可。
- 必要なら `server.py` 引数（例: `--disable-chinese-script-postprocess`）を追加して切替可能化。

### Step 5: ログと検証
- `target_lang`, `mode`, `applied_count`, `error_count` を1行ログ。
- サンプル文で `zh-Hant/zh-TW/zh-HK` の差分確認。

## 8. 非機能要件チェック
- 性能: 文字変換コストは軽量。長文連打でも翻訳時間に対して影響は小さい。
- セキュリティ: ローカル処理のみ（外部送信なし）。
- 可観測性: 変換適用件数と失敗件数をログ化。
- 互換性: Llama以外の翻訳エンジンは無影響。
- 運用: 問題時はLlamaサービス単位で切り戻し可能。

## 9. リスクと緩和策
- Risk: 固有名詞が意図しない字形に変換される。
- Mitigation: 変換後に小規模置換辞書（例: product名）を適用可能なフックを用意。

- Risk: OpenCC依存の配布問題（環境差）。
- Mitigation: 依存関係を `pyproject.toml` に固定し、起動時に import チェックと明示ログを行う。

- Risk: 誤訳を字形変換で隠してしまう。
- Mitigation: デバッグ時に `raw` と `converted` を比較ログで確認できるようにする。

## 10. 影響範囲（変更ファイル候補）
- `TranslationServiceLlama/chinese_script_postprocess.py`（新規）
- `TranslationServiceLlama/llama_engine.py`
- `TranslationServiceLlama/server.py`（任意: トグル引数追加時）
- `TranslationServiceLlama/pyproject.toml`（OpenCC依存追加時）
- `Doc/Llama_ChineseScript_PostProcess_OpenCC_Plan.md`（本書）

## 11. Definition of Done
- [ ] `target_lang=zh-Hant/zh-TW/zh-HK` でLlama出力が繁体系へ変換される。
- [ ] 中国語以外ターゲットは変換されない。
- [ ] 変換失敗時に翻訳全体は失敗せず原文訳を返す。
- [ ] C# 側コード変更なしで表示/キャッシュに変換結果が反映される。
- [ ] ログで適用モードと件数を確認できる。
