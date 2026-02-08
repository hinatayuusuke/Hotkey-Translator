# Gemini Force Strict（UI併用 + F10系Hotkey）実装案

1. **概要（1–3行）**
- Gemini翻訳有効時に、Geminiのみを強制利用する「Strictモード」を追加する。
- 方式は併用: UIで常時強制ON/OFF、Hotkeyでワンショット強制（F10系）を提供する。
- 強制時はフォールバック禁止（失敗時は空結果/エラー扱い）とし、通常実行の経路と明確に分離する。

2. **ゴール / 非ゴール**
- ゴール: ユーザーが明示的にGemini固定を選べる（常時・一時の両方）。
- ゴール: 既存のF10「Force run」運用に寄せた操作感を維持する。
- ゴール: 強制時の挙動をログ/UIで判別可能にする。
- 非ゴール: Gemini翻訳品質改善（prompt調整、モデル変更）。
- 非ゴール: 既存翻訳プロバイダの全面的な優先度アルゴリズム刷新。

3. **前提・仮定**
- 現状、`TranslationFallbackService` は `EnableLlamaCppTranslation=true` の場合にLlamaを最優先で使用し、他は優先度順フォールバック。
- 現状、F10は `RunOnceAsync(new ForceRunOptions(...))` 経路で「skip pHash/OCR diff/cache」を実施。
- Gemini利用可否は `EnableGemini` と API key (`ApiKey`) で判定される。

4. **現状整理**
- Hotkey登録は `MainWindow.TryRegisterHotkeys()` の個別登録＋失敗時ロールバック方式。
- 設定永続は `AppSettings` に集約され、`ApplySettingsToUi` / `OnSettingChanged` でUI連動。
- 翻訳実行は `PipelineOrchestrator` → `TranslationFallbackService.TranslateAsync()` へ渡される。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `AppSettings` に Gemini強制設定と専用Hotkey設定を追加。
  - `MainWindow` に UIトグル、Hotkey登録、ワンショット実行ハンドラを追加。
  - `PipelineOrchestrator` / `ForceRunOptions` に「今回実行だけGemini Strict」を伝播するフラグを追加。
  - `TranslationFallbackService` に Gemini Strict実行分岐を追加。
- データフロー / シーケンス:
  1. 通常実行: 既存挙動（Llama優先 + priority fallback）
  2. UI常時強制ON: 全実行で Gemini Strict 経路に分岐
  3. Hotkeyワンショット: 当該実行のみ `ForceGeminiStrict=true` を付与（F10系）
  4. Strict経路では Gemini 1本のみ試行し、失敗/空結果時はフォールバックしない
- 既存パターンへの整合:
  - Hotkeyは既存の `TryApplyHotkeyBinding` に1本追加して重複検知・ロールバックを再利用。
  - Force runのUXに寄せるため、ワンショットはデフォルト `Shift+F10` とする。

6. **インターフェース設計**
- `AppSettings` 追加項目（案）:
  - `EnableGeminiForceStrictMode: bool = false`（UI常時強制）
  - `HotkeyForceGeminiStrictKey: string = "F10"`
  - `HotkeyForceGeminiStrictModifiers: string = "Shift"`
- `ForceRunOptions` 拡張（案）:
  - `ForceGeminiStrict: bool` を追加（既存既定値は `false`）
- `TranslationFallbackService` API拡張（案）:
  - `TranslateAsync(..., ForceRunOptions options, ...)` もしくは `TranslationExecutionOptions` を追加
- 実行判定ルール:
  - `effectiveForce = settings.EnableGeminiForceStrictMode || options.ForceGeminiStrict`
  - `effectiveForce == true` の場合:
    - Geminiが無効/キー未設定: 実行中断（空返却）し、明確ログを出す
    - Gemini失敗/空結果: フォールバックせず終了（Strict）

7. **実装手順（ステップ分割）**
- Step 1: 設定モデル拡張
  - `Models/AppSettings.cs` に新規設定/Hotkeyを追加。
  - Hotkey正規化・既定値補完ロジックへ組み込み。
- Step 2: UI追加
  - `MainWindow.xaml` Translationパネルに `Gemini Force Strict Mode` チェック追加。
  - Hotkeysパネルに `Force Gemini (strict)` 行を追加（Key + Ctrl/Alt/Shift）。
- Step 3: MainWindow配線
  - `ApplySettingsToUi` / `OnSettingChanged` / `BuildHotkeyConfigFromSettings` / `ApplyHotkeySettingsToUi` を拡張。
  - `OnForceGeminiStrictHotkeyPressed` を追加し、`RunOnceAsync` をF10同等オプション + `ForceGeminiStrict=true` で起動。
  - 起動ログ/更新ログに新ホットキー表示を追加。
- Step 4: 翻訳実行経路拡張
  - `ForceRunOptions` または専用ExecutionOptionsを `PipelineOrchestrator` から `TranslationFallbackService` へ渡す。
  - `TranslationFallbackService` に Gemini Strict分岐を追加（Llama優先分岐より先に評価）。
- Step 5: ガードと可観測性
  - Gemini無効時の強制要求は明示ログ（必要ならToast）を出す。
  - Strict分岐開始/終了ログを追加して実行経路を追跡可能にする。

8. **非機能要件チェック**
- 性能: Strict分岐はフォールバック探索を省くため、失敗時の再試行遅延を抑制可能。
- セキュリティ: API keyの扱いは既存の保存方式（Protected項目）を維持。
- 可観測性: `Translation provider active: Gemini (forced strict)` のようなログで追跡可能化。
- 互換性: 新設定の既定値はOFFにし、既存挙動をデフォルトで不変にする。
- 運用: Hotkey競合時も既存方式に従い当該キーのみ失敗、他キーは維持。

9. **リスクと緩和策**
- Risk: Strict強制中にGemini障害が起きると翻訳結果が空になりやすい。
- Mitigation: UIに「Strictはフォールバック無効」を明記し、ログ/Toastで原因を即時通知。
- Risk: F10系ホットキーの競合で登録失敗する。
- Mitigation: 既存の重複検知とロールバック処理をそのまま利用。
- Risk: Llama優先ロジックと衝突して意図せずLlamaが使われる。
- Mitigation: `effectiveForce` 判定を最上位分岐に置き、優先順位を明確化。

10. **影響範囲**
- `Models/AppSettings.cs` — Gemini Strict設定とHotkey設定を追加。
- `MainWindow.xaml` — Translation設定UI/Hotkey UIを追加。
- `MainWindow.xaml.cs` — 設定反映、Hotkey登録、実行ハンドラ、ログ文言を拡張。
- `Services/PipelineOrchestrator.cs` — 実行オプションの伝播。
- `Services/TranslationFallbackService.cs` — Gemini Strict分岐ロジック追加。

11. **Definition of Done（完了条件チェックリスト）**
- [ ] UIの `Gemini Force Strict Mode` ONで、通常F8実行でもGeminiのみ使用される。
- [ ] `Shift+F10`（既定）で、当該実行のみGemini Strictが発動する。
- [ ] Strict時はGemini失敗でフォールバックせず終了する。
- [ ] 非Strict時は既存の優先度フォールバック挙動が維持される。
- [ ] Hotkey登録失敗が他ホットキーへ波及しない。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
