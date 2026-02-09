# Translation Priority 準拠化（Llama固定解除 + CTranslate2/GoogleWeb整理）実装案

1. **概要（1–3行）**
- 第1段として `EnableLlamaCppTranslation=true` 時の Llama 固定分岐を廃止し、翻訳選択を `TranslationPriority` 準拠へ統一する。
- 第2段として CTranslate2 と GoogleWeb を整理し、UI と既定優先度を現行運用（Local/Gemini/DeepL）へ揃える。
- Llama ホスト常駐（アプリ終了または明示停止まで）は期待仕様として維持する。

2. **ゴール / 非ゴール**
- ゴール: Llama 有効時でも優先度順（例: `LlamaCpp > Gemini > DeepL`）を厳密に適用する。
- ゴール: CTranslate2 を UI/実行経路から外し、GoogleWeb を UI候補から外す。
- ゴール: 既定優先度を `LlamaCpp, Gemini, DeepL` に変更する。
- 非ゴール: ローカルモデル常駐ポリシーの変更（常駐は維持）。
- 非ゴール: 翻訳品質改善（prompt/モデル更新）。
- 非ゴール: 旧 `settings.json` の自動移行実装（開発中は再生成運用を許容）。

3. **前提・仮定**
- 現行 `TranslationFallbackService.TranslateAsync()` には Llama 直行分岐がある。
- `TranslationPriority` は UI から保存され、`NormalizePriority` が既定値補完を行う。
- 各プロバイダは `IsEnabled(settings)` で可否判定できる。
- 本フェーズでは「翻訳時に使うエンジン優先度の整合」を主目的とし、ホスト起動/停止の常駐戦略は変更しない。

4. **現状整理**
- `Services/TranslationFallbackService.cs` 冒頭の `EnableLlamaCppTranslation` 分岐で Llama を直接実行・return している。
- そのため `TranslationPriority` ループへ到達せず、UI優先度が無視される。
- `Models/TranslationProviderNames.cs` の既定値は `LlamaCpp, CTranslate2, Gemini, DeepL, GoogleWeb`。
- 現在の実行時プロバイダ登録は `LlamaCpp / CTranslate2 / DeepL / Gemini` で、GoogleWeb は登録されていない。
- `MainWindow.xaml` には CTranslate2 設定UIが残っている。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `TranslationFallbackService` を単一経路（priority loop）へ一本化。
  - 翻訳プロバイダの有効候補を `LlamaCpp / Gemini / DeepL` 中心へ整理。
  - CTranslate2/GoogleWeb は UI候補から除外する。
- データフロー / シーケンス:
  1. `NormalizePriority(settings)` で候補順序を生成
  2. 上から `provider.IsEnabled` を評価
  3. 成功時点で採用、失敗/空結果なら次へフォールバック
- 既存パターンへの整合:
  - 例外処理方針（`OperationCanceledException` 再送出、それ以外はログして継続）を維持。
  - Llama ホスト常駐の挙動は現行のまま維持。

6. **インターフェース設計**
- 既存公開APIは維持:
  - `TranslationFallbackService.TranslateAsync(IReadOnlyList<string>, AppSettings, CancellationToken)`
- 内部仕様変更:
  - Llama固定分岐を削除。
  - ログ文言を優先度ベースで統一（どの順位を試したか追跡可能化）。
- 優先度候補整理:
  - `TranslationProviderNames.Defaults` を `LlamaCpp, Gemini, DeepL` に更新。
  - CTranslate2 / GoogleWeb は UI の優先度並び替え対象から除外。

7. **実装手順（ステップ分割）**
- Step 1: `TranslationFallbackService` の Llama先頭分岐を削除し、priority loop 一本化。
- Step 2: `MainWindow` の翻訳プロバイダ登録から CTranslate2 を外す（必要な実行経路の参照整理を含む）。
- Step 3: `TranslationProviderNames.Defaults` を `LlamaCpp, Gemini, DeepL` へ変更し、GoogleWeb を優先度UIから非表示化。
- Step 4: CTranslate2 の設定UI/状態表示（Translation status 含む）を削除または非表示化。
- Step 5: ログを強化（選択理由・skip理由・fallback遷移）。

8. **非機能要件チェック**
- 性能: 失敗時フォールバック回数は増える可能性があるが既存機構内で完結。
- セキュリティ: APIキー管理方式への変更なし。
- 可観測性: ログで採用プロバイダとフォールバック遷移を追跡可能化。

9. **リスクと緩和策**
- Risk: 優先度設定が不適切で意図しないプロバイダが先に使われる。
- Mitigation: 起動ログで現在優先度を表示。
- Risk: 開発中に旧 `settings.json` が残り、不要項目が混在する。
- Mitigation: 開発運用として `settings.json` 再生成（削除）を許容し、段階的に整理する。

10. **影響範囲**
- `Services/TranslationFallbackService.cs` — 選択ロジック一本化（主変更）。
- `MainWindow.xaml.cs` — プロバイダ登録、状態表示、ログ文言の整理。
- `MainWindow.xaml` — CTranslate2 UI と GoogleWeb 関連表示の整理。
- `Models/TranslationProviderNames.cs` — 既定優先度の更新。
- （必要時）`Models/AppSettings.cs` — CTranslate2 関連項目の段階的整理。

11. **Definition of Done**
- [ ] Llama有効時でも `TranslationPriority` の先頭プロバイダが最初に実行される。
- [ ] 先頭失敗時は次順位へフォールバックする。
- [ ] Llama固定分岐が削除されている。
- [ ] 既定優先度が `LlamaCpp, Gemini, DeepL` になっている。
- [ ] CTranslate2 は UI から削除/非表示化され、GoogleWeb も優先度UIに表示されない。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
