# WinRT OCR CJKスペース補正 実装案

1. **概要（1–3行）**
- WinRT OCR の `line.Text` に混入する CJK 文字間スペースを、WinRT経路限定で段階的に補正する。
- 第1段階は「CJK間スペースのみ除去」の最小修正で開始し、英語/数字の単語間スペース破壊を避ける。
- 今回は UI 追加を行わず、WinRT内部の補正ロジックのみ実装する。

2. **ゴール / 非ゴール**
- ゴール:
- WinRT OCR 由来の `普 段 で` のような不自然空白を減らす。
- 翻訳送信テキストとオーバーレイ表示の可読性を改善する。
- PaddleOCR など他OCR経路へ副作用を出さない。
- 非ゴール:
- OCR認識精度そのものの改善。
- 言語判定や行結合ロジック（OcrLineGrouper）の全面変更。

3. **前提・仮定**
- 問題は主に WinRT OCR の `line.Text` のトークナイズ起因で、CJK行で1文字ごとに空白が入りやすい。
- 翻訳送信は `PipelineOrchestrator` で `ReadingUnit.Text` を使うため、OCR段で補正すると表示/翻訳の双方に効く。
- 既存ログで空白可視化（`<sp>`）は導入済みなので、前後比較が可能。

4. **現状整理**
- WinRT: CJKの文字間スペースが多発しやすい。
- Paddle: 同問題は相対的に小さく、同じ補正を適用すべきではない。
- 現状では OCRプロバイダから返した `line.Text` をほぼそのまま後段で利用している。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `WinRtOcrProvider`（補正実装の主対象）
- `WinRtOcrProvider` 内部補正ヘルパー（文字列規則を集約）
- データフロー / シーケンス:
1. WinRT OCR が `line.Text` を取得
2. 補正適用条件を満たす行だけ補正関数を通す
3. 補正後テキストで `OcrLine` を生成
4. 既存の行結合・翻訳・表示パイプラインへ流す
- 既存パターン整合:
- 補正対象を WinRT に限定し、Paddle/他プロバイダの契約や挙動を変更しない。

6. **インターフェース設計**
- 補正関数候補:
- `NormalizeWinRtCjkSpacing(string text): string`
- `ShouldApplyCjkSpacingFix(string text, string? sourceLanguage): bool`
- 適用条件（初期）:
- `OcrEngine == WinRT`
- `SourceLanguage` が `ja` / `zh*`、または `line.Text` の CJK率がしきい値以上（例: 0.6）
- 補正ルール（第1段階）:
- 半角空白・全角空白を同一扱いにする
- CJK文字どうしに挟まれた空白のみ削除
- CJKと数字の間の空白は削除（例: `第 3 章` -> `第3章`）
- 英数字どうしの空白は維持（例: `get the game`, `ver 1.0`）
- 句読点の直前空白は削除、直後空白は1つまで許容
- 行頭/行末空白は trim
- 第2段階（条件付き適用）:
- CJK率が閾値以上の行にのみ補正を適用（英数字主体行を保護）

7. **実装手順（ステップ分割）**
- Step 1: `WinRtOcrProvider` に第1段階補正（CJK間スペース除去 + 具体ルール）を実装。
- Step 2: `SourceLanguage` に加えて CJK率フォールバック判定を実装。
- Step 3: 補正前後を比較できる簡易ログを追加（必要時のみ、長文は切り詰め）。
- Step 4: 代表ケースで手動検証し、誤補正が多い場合は CJK率しきい値と句読点ルールを調整。
- Step 5: なお残る場合のみ第3段階（Words再構成）を導入。

8. **非機能要件チェック**
- 性能:
- 文字列走査中心で低コスト（行ごと O(n)）。
- セキュリティ:
- 外部I/Oなし、ローカル文字列処理のみ。
- 可観測性:
- 既存の翻訳送信ログと合わせ、空白混入率の前後比較が可能。
- 互換性:
- WinRT限定なので、Paddle経路へ影響しない。
- 問題発生時は補正呼び出しを1箇所で無効化できる構造にする。

9. **リスクと緩和策**
- Risk: 英数字混在文で必要な空白を誤って削除する。
- Mitigation: 初期は「CJK間空白のみ削除」に限定し、条件判定を厳格にする。
- Risk: ログが増えて運用しづらくなる。
- Mitigation: デバッグログは件数/文字数を制限し、通常は要約ログのみ出す。
- Risk: 言語タグ設定ミス時に補正が効かない/効きすぎる。
- Mitigation: `SourceLanguage` だけに依存せず、CJK率フォールバックを併用する。
10. **影響範囲**
- `Services/WinRtOcrProvider.cs` — CJKスペース補正ロジック追加（WinRT限定）。
- `Doc/Ocr_WinRt_Cjk_Spacing_Fix_Plan.md` — 本計画。

11. **Definition of Done**
- [ ] WinRT + `ja/zh*` で CJK間スペースが改善する。
- [ ] 英語単語間スペース（例: `get the game`）が維持される。
- [ ] PaddleOCR 経路の挙動に変化がない。
- [ ] `SourceLanguage` が非 `ja/zh*` でも、CJK率が高い行で補正が適用される。
- [ ] 代表CJKサンプルで、翻訳送信ログの `text=\"...\"` から CJK間 `<sp>` が消える。
- [ ] 代表英語サンプルで、翻訳送信ログの英単語間 `<sp>` は維持される。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
