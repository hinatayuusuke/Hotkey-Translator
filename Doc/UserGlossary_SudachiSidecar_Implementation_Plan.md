# User Glossary Sudachi Sidecar Implementation Plan

1. **概要（1–3行）**
短語や曖昧語の glossary 誤爆を減らすため、`SudachiPy` を軽量な sidecar として常時利用し、境界・品詞の補助判定だけを glossary 適用前に使う。
通常の長い glossary 語は既存ルールベースで処理し、`ルー` のような危険な短語だけに補助判定を掛ける。

2. **ゴール / 非ゴール**
- ゴール
  - glossary の短語・曖昧語に対して、単純部分一致より安全な適用判定を行えるようにする。
  - 常時動作でも通常翻訳への性能影響を小さく抑える。
  - 既存の `UserGlossaryService` と `TranslateStage` の設計を大きく壊さずに差し込める。
  - Python runtime は既存の `uv` / sidecar パターンに合わせる。
- 非ゴール
  - GiNZA/spaCy 全体を常時フル活用する高コスト NLP パイプライン。
  - 全 glossary 語に対する形態素解析の強制適用。
  - 文全体意味解析や汎用 NER による全面置換。
  - runtime watcher による glossary 再読込。

3. **前提・仮定**
- 現在の glossary は `UserGlossaryService` が通常翻訳前に placeholder 保護する方式で動作している。
- glossary 誤爆の主因は、短い CJK 語や曖昧な名前に対して境界判定が弱いことにある。
- この repo には `uv` と Python sidecar / host を起動する既存パターンがある。
- 常時運用でも、SudachiPy 単体なら GiNZA 全体より軽く扱える前提で進める。

4. **現状整理**
- 現行 glossary
  - 長い語は最長一致である程度安全に扱える。
  - ASCII 語には単語境界判定がある。
  - CJK 語には明示的な形態素境界判定がなく、短語は誤爆しやすい。
- 問題
  - `ルー` のような短い人名は、部分一致だけでは本文や別語の一部にも当たりうる。
  - 一方で glossary を完全一致だけにすると、OCR の揺れや軽微な文脈差に弱くなる。
- 既存の実装資産
  - `SettingsService` / `ResourceHostFacade` / 各 gRPC host は sidecar/host 運用パターンをすでに持っている。
  - glossary は起動時に辞書フォルダから読み込むので、sidecar 用の補助 metadata も同じく起動時固定で扱いやすい。

5. **提案アーキテクチャ**
- コンポーネント構成
  - `Services/Translation/UserGlossaryService.cs`
    - glossary の保護本体と、Sudachi 判定の呼び出し判断を持つ。
  - `Services/Translation/SudachiGlossaryAssistClient.cs`
    - C# 側の軽量 client。
  - `TranslationServiceGlossary/`（新規 Python プロジェクト想定）
    - `uv` 管理の Python sidecar。
    - SudachiPy を使って token boundary / pos 情報を返す。
  - 必要なら `Services/Application/ResourceHostFacade.cs`
    - sidecar の起動停止管理へ統合する。
- データフロー / シーケンス
  1. 起動時に glossary files を読み込む。
  2. 短語・曖昧語と判定される entry を別リストとして前計算する。
  3. 通常翻訳時、`UserGlossaryService.Prepare(...)` が glossary 語を評価する。
  4. 長い語や安全な語は既存ルールだけで処理する。
  5. 短語・曖昧語だけ `SudachiGlossaryAssistClient` へ問い合わせる。
  6. sidecar が token 境界・品詞・周辺 token 情報を返す。
  7. C# 側が glossary 適用可否を最終決定する。
- 既存パターンへの整合
  - provider 本体や `TranslationFallbackService` は変更しない。
  - glossary の適用位置は今と同じく `TranslateStage` 前処理のままにする。
  - sidecar は「翻訳用語判定補助」に限定し、OCR や翻訳 provider の責務へ広げない。

6. **インターフェース設計**
- glossary entry 拡張候補
  - `bool Ambiguous`
    - `true` の場合だけ Sudachi 補助判定対象にする。
  - もしくは `MatchMode`
    - `Simple`
    - `RequireSudachiBoundary`
    - `ExactOnly`
- C# -> sidecar request
  - `string text`
  - `string sourceLanguage`
  - `List<string> candidateTerms`
  - 必要なら `int[] candidateOffsets`
- sidecar response
  - token list
  - 各 token の surface / normalized / part-of-speech
  - candidate term ごとの boundary match 判定
  - 可能なら `proper_noun_like` の補助 flag
- 失敗時
  - sidecar unavailable / timeout / parse error の場合は fail fast にしない
  - fallback は「その短語だけ glossary 適用しない」
  - WHY: 通常翻訳を壊さず、誤爆より未適用を優先するため

7. **実装手順（ステップ分割）**
- Step 1
  - glossary entry に「曖昧語」指定を追加する。
  - 初期は手動で `ルー` のような語だけ対象にする。
- Step 2
  - Python sidecar を新規追加する。
  - SudachiPy を使って文字列を token 化し、候補語が token 境界に一致するかだけ返す最小 API を作る。
- Step 3
  - C# client を追加し、`UserGlossaryService` から曖昧語だけ問い合わせる。
- Step 4
  - glossary 適用判定を三段階に分ける。
  - 長語: 既存ルール
  - ASCII 語: 既存 word boundary
  - 曖昧短語: Sudachi 補助判定
- Step 5
  - 必要なら `ResourceHostFacade` に組み込み、起動時常駐または lazy start を選ぶ。
  - 初期は lazy start でもよいが、「常時軽量利用」を重視するなら app load 時起動を検討する。

8. **非機能要件チェック**
- 性能
  - sidecar 問い合わせは曖昧語ヒット候補がある時だけ行う。
  - 通常の長い glossary 語や glossary 未使用時には Sudachi を呼ばない。
- セキュリティ
  - sidecar はローカル限定 endpoint で十分。
  - 外部入力は OCR/translation source text なので、ログ出力は必要最小限にする。
- 可観測性
  - `glossary_sudachi_check requested=...`
  - `glossary_sudachi_match accepted=...`
  - `glossary_sudachi_skip reason=timeout|unavailable|no_boundary`
- 互換性
  - sidecar が無い環境でも通常翻訳は動くべき。
  - 曖昧語だけが未適用になる設計にする。
- 運用
  - 初期は日本語 source 固定でもよい。
  - 将来、必要なら中国語 source や韓国語 source に拡張する。

9. **リスクと緩和策**
- Risk: SudachiPy sidecar を常時起動すると依存が増え、配布が重くなる。
- Mitigation: 初期は glossary 専用の小 project とし、`uv` で閉じた runtime にする。
- Risk: token 境界だけでは人名かどうかの最終判定に足りない場合がある。
- Mitigation: 初期スコープを「境界判定補助」に限定し、品詞や固有名詞風フラグは後から追加する。
- Risk: sidecar 障害で通常翻訳が止まる可能性がある。
- Mitigation: 曖昧語の glossary 適用だけ skip し、通常翻訳自体は継続する。

10. **影響範囲**
- 変更ファイル候補
  - `Models/UserGlossaryEntry.cs`
  - `Services/Translation/UserGlossaryService.cs`
  - `Services/Translation/SudachiGlossaryAssistClient.cs`（新規）
  - `Services/Application/ResourceHostFacade.cs`（必要なら）
  - sidecar 用 Python project 一式（新規）
- ドキュメント更新
  - glossary file format 説明
  - sidecar セットアップ説明
- 移行
  - 既存 glossary はそのまま動き、曖昧語フラグを使った entry だけ新挙動に乗る形にする。

11. **Definition of Done**
- glossary 未設定時の通常翻訳挙動が現行と変わらない。
- 長い glossary 語は現行どおり動く。
- `ルー` のような曖昧短語は、Sudachi の境界判定を通った時だけ適用される。
- sidecar が落ちていても通常翻訳は継続する。
- `dotnet build .\Hotkey-Translator.sln -c Release` と sidecar 側セットアップ確認が通る。
