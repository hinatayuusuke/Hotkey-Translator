# Translation Priority 準拠化（Llama固定解除）実装案

1. **概要（1–3行）**
- `EnableLlamaCppTranslation=true` 時に発生している Llama 固定分岐を廃止し、翻訳プロバイダ選択を `TranslationPriority` 準拠に統一する。
- CTranslate2 廃止予定を前提に、選択ロジックを将来も変更不要な構造へ整理する。
- 既存UIの優先度設定どおりに実行されることを主目的とする。

2. **ゴール / 非ゴール**
- ゴール: Llama 有効時でも優先度順（例: Gemini > Llama > DeepL）を厳密に適用する。
- ゴール: CTranslate2 廃止時の追加改修を最小化する。
- 非ゴール: 翻訳品質改善（promptやモデル更新）。
- 非ゴール: UIレイアウトの大幅変更。

3. **前提・仮定**
- 現行は `TranslationFallbackService.TranslateAsync()` に Llama 直行分岐がある。
- `TranslationPriority` は UI から保存され、`NormalizePriority` で既定値補完される。
- 各プロバイダは `IsEnabled(settings)` により可否判定できる。

4. **現状整理**
- `Services/TranslationFallbackService.cs` の冒頭で `EnableLlamaCppTranslation` を見て Llama を直接実行・return している。
- そのため `TranslationPriority` ループへ到達せず、UI優先度が無視される。
- `MainWindow` 側の優先度UI自体は保存・表示されている。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `TranslationFallbackService` を単一経路（priority loop）へ一本化。
  - `TranslationProviderNames.Defaults` を CTranslate2 依存が薄い順に見直し可能な形で維持。
- データフロー / シーケンス:
  1. `NormalizePriority(settings)` で候補順序を生成
  2. 上から `provider.IsEnabled` を評価
  3. 成功時点で採用、失敗/空結果なら次へフォールバック
- 既存パターンへの整合:
  - 既存の例外ハンドリング（`OperationCanceledException` は再送出、それ以外はログして継続）を維持。

6. **インターフェース設計**
- 既存公開APIは原則維持:
  - `TranslationFallbackService.TranslateAsync(IReadOnlyList<string>, AppSettings, CancellationToken)`
- 内部仕様変更:
  - Llama固定分岐を削除。
  - ログ文言を `active (by priority)` 形式へ統一して経路可視化を強化。
- 既定優先度（CTranslate2廃止準備）案:
  - `LlamaCpp, Gemini, DeepL`（必要に応じて `GoogleWeb`）

7. **実装手順（ステップ分割）**
- Step 1: `TranslationFallbackService` の Llama 先頭分岐を削除し、priority loop 一本化。
- Step 2: `NormalizePriority` の既定補完順を廃止計画に合わせて調整（CTranslate2 を後方または除外）。
- Step 3: ログを強化（選択理由・skip理由・fallback遷移）。
- Step 4: 必要なら `TranslationStatusText` に「優先度順適用中」の補足表示を追加（任意）。

8. **非機能要件チェック**
- 性能: 失敗時フォールバック回数は増える可能性があるが、既存機構内で完結。
- セキュリティ: APIキー管理には影響なし。
- 可観測性: ログ強化で実際にどのプロバイダが使われたか追跡可能。
- 互換性: Llama有効時の挙動が変更されるため、仕様変更として明示が必要。

9. **リスクと緩和策**
- Risk: 既存利用者が「Llama有効=必ずLlama」の挙動を期待している可能性。
- Mitigation: リリースノート/設定説明に「優先度順が常に有効」を明記。
- Risk: 優先度設定が不適切だと期待しないプロバイダが先に使われる。
- Mitigation: UIで並び替え誘導、起動ログに現在の優先度を出力。

10. **影響範囲**
- `Services/TranslationFallbackService.cs` — 選択ロジックの一本化（主変更）。
- `Models/TranslationProviderNames.cs` — 既定優先度の見直し（必要時）。
- `MainWindow.xaml.cs` — ログ/ステータス補助文言（任意）。

11. **Definition of Done**
- [ ] Llama有効時でも `TranslationPriority` の先頭プロバイダが最初に実行される。
- [ ] 先頭失敗時は次順位へフォールバックする。
- [ ] Llama固定分岐が削除されている。
- [ ] ログで「どの順位が採用されたか」を確認できる。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
