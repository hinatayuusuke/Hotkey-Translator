# User Glossary Term Fix Current Pipeline Implementation Plan

1. **概要（1–3行）**
現行の `TranslateStage` と `CacheKeyBuilder` に合わせて、ユーザ glossary を共通翻訳経路へ差し込む実装案です。
glossary は翻訳前に用語を保護し、翻訳後にユーザ指定訳へ復元する方式で扱います。

2. **ゴール / 非ゴール**
- ゴール
  - DeepL / GoogleWeb / Gemini / Llama 系の通常翻訳経路で、登録用語を同じ訳へ固定する。
  - `TranslateStage` の cache / last-translation 再利用と整合する。
  - glossary 未設定時は現行挙動を維持する。
- 非ゴール
  - `ForceGeminiStrict` の画像直送経路への適用。
  - provider ごとの glossary API 利用。
  - 文全体固定辞書、正規表現辞書、UI の高度な編集機能。

3. **前提・仮定**
- 通常翻訳は `TranslateStage.ExecuteAsync(...)` が `ReadingUnit.Text` を `NormalizeForTranslation()` した後に provider へ送っている。
- cache key はすでに `GlossaryVersion` を含んでいる。
- `TranslateStage` は cache 以外に `_lastTranslations` でも直近訳を再利用している。
- `ForceGeminiStrict` は現在 `TranslateStage` を通らないため、本実装の対象外とする。

4. **現状整理**
- 現行挙動
  - `TranslateStage` は `translationSourceText` を作り、`normalized` を cache key と `_lastTranslations` のキーに使っている。
  - provider 呼び出しは `TranslationFallbackService.TranslateAsync(IReadOnlyList<string> texts, ...)` で行う。
  - cache key は `SourceLanguage_TargetLanguage_StyleId_GlossaryVersion_normalizedText`。
- 問題
  - glossary を入れると、cache だけでなく `_lastTranslations` も glossary 変更の影響を受ける。
  - provider 戻り値が `sourceText -> translatedText` 辞書なので、保護前後の文字列対応を `TranslateStage` 側で持つ必要がある。

5. **提案アーキテクチャ**
- コンポーネント構成
  - `Models/UserGlossaryEntry.cs`
  - `Services/Translation/UserGlossaryService.cs`
  - `Models/AppSettings.cs`
  - `Services/Orchestration/Stages/TranslateStage.cs`
- データフロー
  1. `TranslateStage` が `unit.Text` を `NormalizeForTranslation()` して `translationSourceText` を作る。
  2. `UserGlossaryService` が `translationSourceText` に対して glossary 保護を適用し、`protectedSourceText` を返す。
  3. cache / `_lastTranslations` は glossary version を含むキーで参照する。
  4. provider へは `protectedSourceText` を送る。
  5. provider の戻り値を `UserGlossaryService` が復元し、最終訳へ変換する。
  6. cache 保存と `_lastTranslations` 更新は復元後の最終訳で行う。

6. **インターフェース設計**
- `UserGlossaryEntry`
  - `string SourceTerm`
  - `string TargetTerm`
  - `string? SourceLanguage`
  - `string? TargetLanguage`
  - `bool Enabled`
  - `int Priority`
- `UserGlossaryService`
  - `GlossaryPreparedText Prepare(string translationSourceText, AppSettings settings)`
  - `string Restore(string translatedText, GlossaryPreparedText prepared, AppSettings settings)`
- `GlossaryPreparedText`
  - `string OriginalSourceText`
  - `string ProtectedSourceText`
  - `IReadOnlyList<GlossaryReplacement>` Replacements
- `GlossaryReplacement`
  - `string SourceTerm`
  - `string TargetTerm`
  - `string Placeholder`

7. **実装手順（ステップ分割）**
- Step 1
  - `UserGlossaryEntry` と `UserGlossaryService` を追加する。
  - glossary 未設定時は `ProtectedSourceText == OriginalSourceText` を返す no-op 実装にする。
- Step 2
  - `TranslateStage.PendingTranslation` を拡張する。
  - 最低限以下を保持する:
    - `UnitId`
    - `OriginalSourceText`
    - `ProtectedSourceText`
    - `NormalizedKey`
    - `CacheKey`
    - `GlossaryPreparedText`
- Step 3
  - provider 送信を `ProtectedSourceText` ベースへ切り替える。
  - 戻り値辞書の lookup も `ProtectedSourceText` キーで行う。
- Step 4
  - `UserGlossaryService.Restore(...)` を通した最終訳だけを cache と `_lastTranslations` に入れる。
  - WHY: glossary 変更後に provider 生結果を再利用すると、用語固定が崩れるため。
- Step 5
  - `_lastTranslations` のキーを `normalized` 単体ではなく glossary version を含む形へ変える。
  - 例: `"{settings.GlossaryVersion}_{normalized}"`.
- Step 6
  - 必要なら簡易 UI を追加するが、初期は settings 編集前提でもよい。

8. **非機能要件チェック**
- 性能
  - 初期は glossary 件数を数十〜百件程度と想定し、単純走査で開始する。
  - 多くなった場合のみ Trie 化を検討する。
- 可観測性
  - `glossary_prepare hits=...`
  - `glossary_restore restored=...`
  - `glossary_restore miss=...`
- 互換性
  - glossary 未設定時は完全 no-op。
  - `ForceGeminiStrict` 画像直送には効かないことを仕様として明記する。

9. **リスクと緩和策**
- Risk: OCR ノイズで短い語へ誤ヒットする。
- Mitigation: 最長一致優先、短すぎる語の登録を避ける、必要なら最小文字数ガードを追加する。
- Risk: provider が placeholder を壊して復元できない。
- Mitigation: 壊れにくい ASCII placeholder を使い、未復元時はログを残してその文だけ通常訳へ寄せる。
- Risk: glossary 更新後に `_lastTranslations` が古い訳を返す。
- Mitigation: glossary version を `_lastTranslations` のキーにも含める。

10. **影響範囲**
- `Models/AppSettings.cs`
- `Models/UserGlossaryEntry.cs`
- `Services/Translation/UserGlossaryService.cs`
- `Services/Orchestration/Stages/TranslateStage.cs`
- `Services/CacheKeyBuilder.cs` は既存 `GlossaryVersion` を流用するので大きな変更は不要
- 必要なら settings UI

11. **Definition of Done**
- 通常翻訳経路で glossary 登録語が provider に依らず同じ訳へ固定される。
- glossary 未設定時の挙動が現行と一致する。
- `GlossaryVersion` 更新時に cache と `_lastTranslations` の stale reuse が起きない。
- `ForceGeminiStrict` 画像直送が対象外であることが仕様として明確になっている。
- `dotnet build .\Hotkey-Translator.sln -c Release` が通る。
