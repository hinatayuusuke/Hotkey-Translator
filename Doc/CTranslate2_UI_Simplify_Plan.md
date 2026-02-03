# CTranslate2 翻訳UI簡略化 + 自動再起動実装案

1. **概要（1–3行）**
- CTranslate2 翻訳設定を UI で ON/OFF + Device(CPU/GPU) のみに簡略化する。
- Precision は固定（CPU=int8 / GPU=fp16）、Auto-download は常時ONとする。
- Device変更時は自動で翻訳サーバを再起動し、進捗/失敗をログに出す。

2. **ゴール / 非ゴール**
- ゴール: UIの簡略化と迷いの排除。
- ゴール: Device切替を確実に反映（自動再起動）。
- ゴール: 自動DLを前提に、初回DL中の状態を表示。
- 非ゴール: モデルID/host/port/dir をユーザに公開すること。

3. **前提・仮定**
- CTranslate2 gRPC サーバは TranslationService として分離済み。
- フォールバック翻訳は既存のまま維持される。

4. **現状整理**
- 既に CTranslate2 の UI 設定項目を追加済みだが、詳細項目が多い。
- Precision/Auto-download のスイッチが存在している。

5. **提案アーキテクチャ**
- UIからは「Enable」「Device」だけを公開。
- 内部設定は固定値:
  - `precision`: CPU=int8 / GPU=fp16
  - `auto_download`: true
  - `model_id`: 既定の NLLB200 600M
  - `host/port`: 既定の localhost:50061

6. **インターフェース設計**
- UI: `EnableCTranslate2Check`, `CTranslate2DeviceBox` のみ残す。
- 設定は `AppSettings` に残しつつ、UIからは更新しない項目は内部固定にする。

7. **実装手順（ステップ分割）**
- Step 1: UIから詳細項目（Auto-download / ModelID / ModelDir / Precision / Host/Port）を削除。
- Step 2: 設定保存時に Precision と Auto-download を固定値で上書き。
- Step 3: Device 変更時に翻訳サーバを自動再起動。
- Step 4: サーバ起動中は「Downloading/Starting…」をログ表示。失敗時はログ通知。

8. **非機能要件チェック**
- 性能: CPU int8 / GPU fp16 固定で安定。
- 可観測性: 起動・DL中ログを必ず出す。

9. **リスクと緩和策**
- Risk: オフライン環境ではDL失敗。
- Mitigation: ログ通知 + フォールバック。

10. **影響範囲**
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `Models/AppSettings.cs`
- `Services/CTranslate2GrpcHost.cs`

11. **Definition of Done**
- UIは ON/OFF + Device のみ。
- Device切替で自動再起動。
- 自動DLが常時有効で、失敗はログに出る。
