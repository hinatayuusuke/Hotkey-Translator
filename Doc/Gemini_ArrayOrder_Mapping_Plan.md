# Gemini配列順マッピング化（source_text照合廃止）実装案

1. **概要（1–3行）**
- Gemini翻訳の戻りを `source_text` 文字列一致ではなく、入力順インデックスで対応づける方式へ変更する。
- レスポンスJSONを最小化し、生成負荷とフォーマット揺れを減らす。
- `IReadOnlyDictionary<string,string>` 契約は維持し、`GeminiClient` 内で `texts[i] -> translations[i]` に再マップして返す。
- 件数不一致や空要素に対する救済（先頭N部分適用）を入れ、オーバーレイ反映率を安定化する。

2. **ゴール / 非ゴール**
- ゴール: `source_text` 不一致による「翻訳成功なのに英語表示」の取りこぼしを解消する。
- ゴール: Geminiプロンプトとレスポンスのトークン量を削減し、負担を軽くする。
- ゴール: ログは最小化しつつ、失敗時の調査可能性を維持する。
- ゴール: 既存の翻訳プロバイダ共通契約（辞書返却）を維持し、影響範囲をGemini経路に限定する。
- 非ゴール: OCR結果（ReadingUnit生成）そのもののアルゴリズム変更。
- 非ゴール: Llama/DeepL/CTranslate2 のレスポンス仕様変更。

3. **前提・仮定**
- OCR入力は `ReadingUnit` ベースで処理され、翻訳要求は `pendingTexts` の順序を保持している。
- 現在のGemini実装は `{"translations":[{"source_text":"...","translated_text":"..."}]}` を想定している。
- 反映側で `source_text` 完全一致を要求するため、改行/空白差分で不一致が発生し得る。

4. **現状整理**
- `Services/GeminiClient.cs`:
  - プロンプトは source/target を含む長めのルール文。
  - スキーマは `source_text + translated_text` のオブジェクト配列。
  - パース結果は `Dictionary<source_text, translated_text>`。
- `Services/PipelineOrchestrator.cs`:
  - `results.TryGetValue(item.SourceText, out translated)` で一致しない場合は破棄。
- ログ:
  - 現在は raw dump を常時保存しており、通常運用で冗長。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - Geminiレスポンスを「文字列配列」へ簡素化。
  - `GeminiClient` 内で index ベースに `texts[i]` と `translations[i]` を対応づけ、辞書へ再マップ。
  - `PipelineOrchestrator` は既存の辞書適用ロジックを維持（変更最小化）。
- データフロー / シーケンス:
  1. 入力 `texts[]` を Gemini に送信
  2. 出力 `{"translations":["...", "..."]}` を受信
  3. `GeminiClient` で index ごとに `texts[i] -> translations[i]` を辞書化
  4. 不足/余剰/空文字を救済ルールで吸収
- 既存パターンへの整合:
  - `ReadingUnit` と `pendingTexts` の順序性をそのまま利用。
  - キャッシュ保存・オーバーレイ更新フローは維持。

6. **インターフェース設計**
- Gemini要求/応答:
  - 入力: `texts[]`
  - 出力: `{"translations":["translated1","translated2", ...]}`
- スキーマ:
  - 旧: `[{source_text, translated_text}]`
  - 新: `{"translations":["..."]}`
- 返却契約（外部）:
  - 維持: `IReadOnlyDictionary<string,string>`
  - 実装: `GeminiClient` 内部で配列を辞書へ再マップして返却
- マッピング規約:
  - `translations.Count == texts.Count`: 1対1で適用
  - 少ない場合: 取れた分のみ先頭から適用（先頭N部分適用）、残りは原文維持
  - 多い場合: 超過分を破棄
  - 空要素: 原文維持（未翻訳扱い）
  - 重複原文: 既存辞書契約上は最後の値が有効になるため、GeminiClient側で同一キー上書きを許容する

7. **実装手順（ステップ分割）**
- Step 1: `GeminiClient` の `responseSchema` を最小配列形へ変更。
- Step 2: `BuildPrompt` を短文化（JSON only / 同数 / target language の3点）。
  - source language 指示は削除または弱い補足へ変更。
- Step 3: `ParseTranslations` を `List<string>`（順序保持）ベースへ変更。
- Step 4: `GeminiClient` で `texts[i] -> translations[i]` の辞書再マップを実装。
  - `i < min(texts.Count, translations.Count)` の範囲のみ適用（先頭N部分適用）。
  - 空要素は辞書へ入れず、既存フローで原文維持に倒す。
- Step 5: ログ最小化。
  - 常時ログ: `status`, `latency`, `count(in/out)` のみ
  - raw dump 保存: 失敗時のみ（HTTP非成功、JSON抽出失敗、`count(out) < count(in)` など）
  - 追加ガード: 件数不一致時は `count(in/out)` を必ず記録

8. **非機能要件チェック**
- 性能: スキーマ軽量化でトークン削減、パースコスト低減。
- 可観測性: 失敗時のみ詳細dumpを残し、通常ログは簡潔化。
- 互換性: Gemini経路のみ内部実装を変更し、共通返却契約は維持。
- 運用: 反映失敗率低下により、手動再実行頻度を抑制。

9. **リスクと緩和策**
- Risk: Geminiが配列長を守らない場合、先頭N部分適用により後半未反映が発生する。
- Mitigation: 先頭N部分適用を明示仕様化し、未反映分は原文フォールバック。`count(in/out)` を常時記録。
- Risk: プロンプト短文化で一部文脈品質が低下する可能性。
- Mitigation: 最低限の翻訳ルール（JSON only / same length / target language）は維持。
- Risk: raw dump条件を絞ることで、平常時の追跡情報が減る。
- Mitigation: 必要時のみ debug flag で常時保存を復帰できる余地を残す。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- `Services/GeminiClient.cs` — プロンプト短文化、スキーマ変更、配列パース、辞書再マップ、ログ最小化。
- （任意）`Doc/*` — Gemini応答仕様変更の補足を追記。

11. **Definition of Done（完了条件）**
- [ ] Gemini翻訳成功時に `source_text` 不一致で取りこぼさない。
- [ ] `translations` 件数不足/過剰/空要素でクラッシュせず救済できる。
- [ ] 共通返却契約 `IReadOnlyDictionary<string,string>` を維持し、他プロバイダに影響を出さない。
- [ ] 通常ログは `status`, `latency`, `count(in/out)` 中心に簡素化される。
- [ ] raw dump は失敗時のみ保存される。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
