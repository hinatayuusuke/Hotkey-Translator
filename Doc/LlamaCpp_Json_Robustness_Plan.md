# LlamaCpp JSON堅牢化 実装案（Grammarフォールバック / 短縮スキーマ / パーサー救済）

1. **概要（1–3行）**
- Llama出力の JSON 崩れを減らすため、`response_format(json_schema)` を維持しつつ、失敗時に `grammar` 制約へ自動フォールバックする。
- 併せて、モデル負荷を下げる短縮スキーマ（短いキー名）を導入し、小型モデルでの構造破綻率を抑える。
- 最後にパーサー救済を強化し、軽微な崩れ（改行エスケープ・末尾ノイズ・括弧過不足）を回復して空返却率を下げる。
- 単件入力は構造化出力を要求せず、プレーンテキスト翻訳へ切り替えて脱線を抑える。

2. **ゴール / 非ゴール**
- ゴール: 複数件翻訳における JSON 崩れ率を低下させる。
- ゴール: 崩れが発生しても、既存分割フォールバック前に可能な限り回復して成功率を上げる。
- ゴール: 既存UI/既存設定を壊さずに段階導入できる構成にする。
- ゴール: 単件翻訳で JSON 強制を外し、余計な構文出力を減らす。
- 非ゴール: 翻訳内容品質（語彙・自然さ）の根本改善。
- 非ゴール: モデル自体の変更（別モデルへの差し替え）。

3. **前提・仮定**
- 現在の `llama-server.exe` は `--json-schema` / `--grammar` をサポートしている（実機確認済み）。
- OpenAI互換 `/v1/chat/completions` で `response_format` および `grammar` 指定が有効である。
- 既存の `TranslationServiceLlama/llama_engine.py` に分割・再試行フォールバックが実装済み。

4. **現状整理**
- 現在は複数件バッチ時のみ `response_format(json_schema)` を付与している。
- JSON崩れ時はパース失敗→分割再試行へ進むが、救済処理は最小で取りこぼしがある。
- スキーマキーが長く、1.8B級モデルではフォーマット維持コストが高い。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `build_short_json_schema_response_format()`（新規）
  - `build_short_grammar()`（新規）
  - `translate_batch_with_constraints()`（既存 `_translate_batch_once` 内で段階実行）
  - `translate_single_plain()`（既存単件経路の明示化）
  - `parse_batch_translation_content_resilient()`（既存パーサー拡張）
- データフロー / シーケンス:
  1. 単件 (`len(texts)==1`): プレーンテキスト翻訳（JSON制約なし）
  2. 複数件: 1回目を `response_format(json_schema)` で送信
  3. 失敗時: 同一入力を `grammar` 制約で再送（1回）
  4. 受信後: 救済パーサーで修復を試みる
  5. それでも失敗時: 既存の分割フォールバックへ移行
- 既存パターン整合:
  - 既存の `adaptive split` と競合しないよう、単一バッチ内の再送回数を上限1回に制限

6. **インターフェース設計**
- 単件時出力:
  - JSONを要求せず、モデルのプレーンテキストをそのまま単件訳として扱う。
  - 単件時は `response_format` と `grammar` を送らない（制約は複数件専用）。
  - 単件時は受信テキストをそのまま返す（JSONパーサー経由にしない）。
- 内部出力スキーマ（短縮）:
  - 旧: `{"translations":[{"index":0,"translated_text":"..."}]}`
  - 新: `{"t":[{"i":0,"x":"..."}]}`
- 互換パース:
  - パーサーは旧キーと新キーの両方を受理（`translations/index/translated_text` と `t/i/x`）
- フォールバック制御:
  - `json_schema` 失敗時のみ `grammar` を追加して再送
  - `grammar` でも失敗したら既存の分割ロジックへ

7. **実装手順（ステップ分割）**
- Step 1: 単件時プレーンテキスト経路を明示し、JSON制約を複数件に限定。
  - 単件時のAPIペイロードから `response_format` / `grammar` を除外。
  - 単件時レスポンスは JSON パースを行わず、従来のプレーンテキスト整形のみ適用。
- Step 2: 短縮スキーマ定義関数を追加（`t/i/x`）。
- Step 3: バッチ送信を `json_schema -> grammar` の2段階試行へ拡張。
- Step 4: パーサーを拡張し、以下の救済を追加:
  - `\r\n` 等のエスケープ改行の正規化
  - 先頭/末尾ノイズ除去（最外JSON抽出強化）
  - 末尾余剰 `}` の切り詰め再試行
  - 配列要素で `i/x` と `index/translated_text` の両対応
- Step 5: ログ強化（どの段階で成功したかを記録）
  - `plain_single_success`, `schema_success`, `grammar_fallback_success`, `parser_rescue_success`, `split_fallback`
- Step 6: 既存テストスクリプトで回帰確認

8. **非機能要件チェック**
- 性能: 追加再送は失敗時のみ（通常は1回送信維持）。
- セキュリティ: 入出力構造のみ変更で外部送信先変更なし。
- 可観測性: 段階別成功率ログで効果測定可能にする。
- 互換性: 旧キー受理で段階移行時の互換性を維持。
- 運用: モデル差異で挙動がぶれるため、段階別ログを運用指標にする。

9. **リスクと緩和策**
- Risk: grammar定義が厳しすぎると生成不能で失敗率が上がる。
- Mitigation: 最小文法（必要キーのみ）で開始し、制約は段階的に強化。
- Risk: 救済パーサーが過剰に修復して誤データを通す可能性。
- Mitigation: `index` 範囲チェック・件数整合チェックを必須にし、不整合は即失敗扱い。
- Risk: 再送追加で遅延が増える。
- Mitigation: `grammar` 再送は1回限定、失敗時は即分割へ遷移。

10. **影響範囲**
- `TranslationServiceLlama/llama_engine.py`
  - スキーマ短縮定義
  - grammarフォールバック送信
  - パーサー救済強化
  - ログ追加
- （任意）`TranslationServiceLlama/test_translation_engine.py`
  - 短縮スキーマ受理とフォールバック系の簡易検証追加

11. **Definition of Done**
- 単件翻訳では JSON 文字列（`{"translations"...}` 等）が画面へ露出しない。
- 単件翻訳リクエストの送信ログに `response_format` / `grammar` が含まれない。
- 複数件翻訳で JSON 崩れ時に `grammar` 再送が実行されることをログで確認できる。
- 旧スキーマ/短縮スキーマの両方をパーサーが受理できる。
- 既存の分割フォールバックが退行していない。
- 既存UI変更なしで動作し、`py_compile` と実行確認が通る。

## 参考実装方針（最小）
- 第1段階は `llama_engine.py` 単体で完結させる（UI/設定追加なし）。
- 段階導入後、必要なら設定トグル（`EnableLlamaGrammarFallback` など）を追加する。
