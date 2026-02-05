# PaddleOCR / CTranslate2 条件付きロード & ロード画面（設定確定時）実装案

1. **概要（1–3行）**
設定確定時にのみPaddleOCR/CTranslate2を条件付きでロードし、以降は再起動まで常駐状態を固定する。
ロード中は既存の「OCR Running」表示を流用して待機状態を明示し、失敗時はダイアログで通知して設定をOFFへ戻す。

2. **ゴール / 非ゴール**
- ゴール: 起動時の初期化を軽量化し、必要時のみロードすることで起動時間・VRAM使用の無駄を減らす。
- ゴール: ロード中の待機状態をUIで明示し、失敗時は安全にOFFへ戻す。
- 非ゴール: 実行中にロード済み資源のアンロード（VRAM解放）を行う。
- 非ゴール: ホットキー実行時に自動ロードする挙動の導入。

3. **前提・仮定**
- 設定確定（保存/適用）イベントが存在し、そこで初期化のフックが可能である。
- OCR Running 表示（ロード画面に流用できるUI）が既に存在する。
- PaddleOCR / CTranslate2 のロード処理は非同期で実行可能。

4. **現状整理**
- 現状は起動時にPaddleOCR/CTranslate2をロードしている。
- ロード中のUIはOCR Running表示が利用可能。
- 失敗時のUIと設定のロールバック動作は未整備。

5. **提案アーキテクチャ**
- コンポーネント構成: Settings適用処理 -> LazyLoadCoordinator（新規） -> PaddleOcrLoader / CTranslate2Loader -> OCR Running表示 + DialogService。
- データフロー/シーケンス: 設定確定 -> ロード画面表示 -> 条件に応じて順次ロード -> 成功で終了 / 失敗でOFF更新 + ダイアログ。
- 既存パターンへの整合: AppLogger / 既存非同期設計 / OCR Running 表示の流用。

6. **インターフェース設計**
- API: `LazyLoadCoordinator.EnsureLoadedAsync(AppSettings settings)` を追加し、設定確定時に呼ぶ。
- エラー: ロード失敗時は例外を捕捉し、対象設定をOFFに戻した上でダイアログ表示。
- UI文言（推奨）: "Resources are released on the next launch. Turn OFF and restart the app to free memory."
- 失敗時ダイアログ例: "Failed to load PaddleOCR. The setting has been turned OFF. See the logs for details."

7. **実装手順（ステップ分割）**
- Step 1: 設定確定のフックポイントを特定し、`LazyLoadCoordinator` を呼ぶ。
- Step 2: PaddleOCR/CTranslate2のロードを `EnsureLoadedAsync` に切り出し、二重ロードを防止。
- Step 3: OCR Running 表示をロード中に流用（文言差し替えは "Loading..." など、UIスレッドで表示/非表示）。
- Step 4: 失敗時に設定をOFFへ戻し、ダイアログ通知を実装。
- Step 5: 設定画面に "Restart required" 系の注意文言を表示。

8. **非機能要件チェック**
- 性能: 起動時負荷を軽減。ロードは設定確定時にのみ実行。
- セキュリティ: 外部プロセスやモデル読み込みの失敗時に安全にOFFへ戻す。
- 可観測性: 失敗理由はログ出力し、ダイアログは簡潔に。
- 互換性: 既存の起動時ロード依存を壊さないよう、設定未変更時は従来挙動に近い動き。
- 運用: 失敗時の再試行は再起動＋設定ONで再ロード。

9. **リスクと緩和策**
- Risk: 設定OFF後もVRAM解放されずユーザーが混乱する。
- Mitigation: 明確な文言表示（再起動が必要）とUI上の注意文を常設。
- Risk: ロード中にUIがブロックされる。
- Mitigation: ロード処理は必ず非同期で実行し、UIは待機表示のみ。

10. **影響範囲**
- 変更候補ファイル: `Services/SettingsService.cs` / `Services/OcrEngine.cs` / `Services/CTranslate2GrpcHost.cs` / UI関連（OCR Running表示）。
- ドキュメント更新: 本計画書。

11. **Definition of Done**
- 設定確定時にのみPaddleOCR/CTranslate2がロードされる。
- ロード中はOCR Running表示に "Loading..." 文言が出る。
- 失敗時は対象設定がOFFに戻り、ダイアログで通知される。
- "Resources are released on the next launch. Turn OFF and restart the app to free memory." 文言がユーザーに明示される。
