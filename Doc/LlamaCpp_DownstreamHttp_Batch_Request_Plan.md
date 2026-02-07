# Llama 下流HTTP一括送信化 実装案

1. **概要（1–3行）**
- `TranslationServiceLlama/llama_engine.py` を改修し、受信した `texts[]` を原則1回の `/v1/chat/completions` でまとめて翻訳する。
- ただし、UI設定（`LlamaBatchSize`, `LlamaMaxTokens`, `LlamaContextSize`）を超過する見込み時のみ、自動で分割送信する。
- 上位（WPF→gRPC）の「1リクエスト送信」は維持し、下流HTTPの往復回数を最小化する。

2. **ゴール / 非ゴール**
- ゴール: `texts.Count >= 2` でも下流HTTPが原則1回になること。
- ゴール: 制約超過時のみ安全に分割し、翻訳欠落を防ぐこと。
- ゴール: 同一文字列が複数含まれるケースでも順序崩れなく戻せること。
- 非ゴール: WPF側の翻訳パイプラインやプロバイダ選択ロジックの変更。
- 非ゴール: llama.cppサーバやモデル自体の入れ替え。

3. **前提・仮定**
- 現行の gRPC インターフェース（`repeated string texts` / `repeated string translations`）は変更しない。
- llama-server は OpenAI 互換 `POST /v1/chat/completions` を利用する。
- LLMは厳密JSONを常に返す保証がないため、パース失敗時フォールバックが必要。
- 設定値の実体は `AppSettings` の `LlamaBatchSize`, `LlamaMaxTokens`, `LlamaContextSize` を参照する。

4. **現状整理**
- 現在は gRPC で複数 `texts` を受けても、`llama_engine.py` が `for text in texts` で1件ずつHTTP送信している。
- そのため `Translation pending: 2 items` でも、下流ログには `POST /v1/chat/completions` が2回出る。
- 順序対応は実質 index 依存だが、HTTP層でまとめ送信する処理は未実装。

5. **提案アーキテクチャ**
- 追加コンポーネント:
  - `translate_batch_once()`：複数要素を1回送信し、配列で受け取る。
  - `estimate_batch_cost()`：入力サイズ見積り（上限超過判定）。
  - `split_for_limits()`：超過時にバッチを分割（2分割再帰）。
  - `translate_with_adaptive_split()`：一括→失敗時分割の実行オーケストレーション。
- データフロー:
  1. `texts[]` を受領。
  2. 上限内見込みなら1回送信。
  3. 超過見込みまたはJSON不整合時は、半分に分割して再試行。
  4. 出力は index で再結合し、元の `texts[]` 長と順序を厳密維持。
- 既存パターン整合:
  - `LlamaTranslator.translate()` の公開シグネチャは維持し、内部実装のみ差し替える。

6. **インターフェース設計**
- 外部I/F（変更なし）:
  - `LlamaTranslator.translate(texts, source_lang, target_lang) -> List[str]`
- 内部送信JSON（新設方針）:
  - 入力は `[{"index":0,"text":"..."}, ...]` を `user` メッセージ内へ埋め込み。
  - 出力は `{"translations":[{"index":0,"translated_text":"..."}, ...]}` を要求。
- パース戦略:
  - 第一優先: `index` で復元。
  - 第二優先: 配列長一致時に順序復元。
  - 失敗時: 分割再試行、最終的に空要素で長さ整合を維持。
- WHY:
  - `source_text` キー照合は重複文で衝突するため、index を正とする。

7. **実装手順（ステップ分割）**
- Step 1: `llama_engine.py` にバッチプロンプト生成関数とJSON抽出関数を追加。
- Step 2: 単発一括送信 `translate_batch_once()` を実装（HTTP1回）。
- Step 3: 上限判定ロジック `estimate_batch_cost()` と `split_for_limits()` を実装。
- Step 4: `translate()` を `translate_with_adaptive_split()` ベースへ置換。
- Step 5: ログを追加（`batch_items`, `http_calls`, `split_depth`, `fallback_reason`）。
- Step 6: `TranslationServiceLlama/test_translation_engine.py` で回帰確認（既存 `grpc` と比較）。

8. **非機能要件チェック**
- 性能: HTTP往復削減でレイテンシ低減を狙う。超過時のみ分割で劣化を限定。
- セキュリティ: 既存ローカル通信（127.0.0.1）を維持し、外部送信先追加なし。
- 可観測性: 「1 gRPC内で何回HTTP送ったか」を必ずログ化。
- 互換性: gRPC schema と WPF 側コードは非変更。
- 運用: JSON崩れ時も完全失敗ではなく段階的フォールバックで復旧可能にする。

9. **リスクと緩和策**
- Risk: モデルが厳密JSONを返さず、配列復元に失敗する。
- Mitigation: `index` 必須指示 + 抽出補正 + 分割再試行 + 最終長さ保証。
- Risk: 一括送信でトークン超過し、応答失敗または品質劣化。
- Mitigation: 事前見積りで分割、かつHTTPエラー時に自動2分割。
- Risk: 大型バッチで1回失敗時の再試行コストが増える。
- Mitigation: 分割深度上限と fail-fast 条件を設ける。

10. **影響範囲**
- `TranslationServiceLlama/llama_engine.py`
  - 一括送信実装、分割戦略、出力復元ロジック、ログ強化。
- `TranslationServiceLlama/test_translation_engine.py`
  - （必要時）新挙動の検証補助ログを追加。
- `Doc/`
  - 本計画書（本ファイル）。

11. **Definition of Done**
- 2要素以上入力時、通常ケースで下流HTTPが1回であることをログで確認できる。
- `LlamaBatchSize`/`LlamaMaxTokens`/`LlamaContextSize` 超過見込み時にのみ分割される。
- 返却 `translations` が常に入力と同じ要素数・順序で返る。
- 同一文重複（同じ `text` が複数）でも取り違えが起きない。
- 失敗注入時（JSON崩れ/HTTP失敗）に、完全空返却ではなく分割フォールバックが機能する。

## 補足（実装ポリシー）
- 「まず1回送る」「ダメなら分割」の順にし、平常時の速度を優先する。
- 分割条件は固定値ではなく、UI設定値を上限として用いる。
- 互換性優先のため、既存 public API と gRPC schema は変更しない。
