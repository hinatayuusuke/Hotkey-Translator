# LlamaCpp モデル選択UI 実装案

1. **概要（1–3行）**
- `TranslationServiceLlama\LlamaCpp\Models` 配下の `.gguf` を検出し、WPF UI から選択できるようにする。
- 選択結果は設定へ保存し、Llama 起動時に `--model` へ反映する。
- 既存の自動ダウンロード（manifest）との整合を保ちつつ、ユーザー配置モデル優先で運用できる形にする。

2. **ゴール / 非ゴール**
- ゴール: ユーザーが `Models` フォルダ内のモデルをUIで選択できる。
- ゴール: 次回起動時も選択モデルを復元し、Llama gRPC 起動に使用される。
- ゴール: 選択モデルが消えた場合に安全にフォールバックし、起動不能を避ける。
- 非ゴール: `Models` 外パスの任意指定（セキュリティ/運用統制のため対象外）。
- 非ゴール: モデル別の推奨パラメータ（ctx/gpu-layers等）の自動最適化。
- 非ゴール: UIでのモデル自動ダウンロード機能追加（既存manifest運用の範囲外）。

3. **前提・仮定**
- モデル配置先は固定で `TranslationServiceLlama\LlamaCpp\Models`。
- 対象拡張子は `.gguf`（大文字小文字は無視）。
- 既存設定は `AppSettings` JSON で保存される。
- Llama 起動引数は `Services/LlamaGrpcHost.cs` が組み立てる。

4. **現状整理**
- 現在は `Services/LlamaGrpcHost.cs` の `FixedLlamaModelRelativePath` で単一モデル固定。
- UI は「model path are fixed by app」と表示され、選択UIがない。
- `model_manifest.json` は固定ファイル名との一致チェックを行うため、別モデル運用に未対応。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `LlamaModelCatalog`（新規サービス）:
    - `Models` ディレクトリを走査して候補一覧を返す。
    - 表示用（ファイル名）と内部用（フルパス）を管理。
  - `AppSettings` 拡張:
    - `LlamaSelectedModelFileName`（例: `qwen3-1_7b-instruct-q4_k_m.gguf`）を追加。
  - `LlamaGrpcHost` 拡張:
    - 固定値ではなく、設定から選択モデルを解決して `--model` に反映。
- データフロー / シーケンス:
  1. 設定読込後に `LlamaModelCatalog` が `Models` 配下を走査。
  2. UI `ComboBox` に候補を表示。
  3. ユーザー選択を `LlamaSelectedModelFileName` に保存。
  4. Llama 起動時に選択値を検証し、存在する `.gguf` を `--model` へ渡す。
  5. 選択値が無効なら既定モデルへフォールバックし、ログを残す。
- 既存パターンへの整合:
  - Paddle のモデル選択UI（`ComboBox` + 設定保存）に合わせる。
  - 既存の `Normalize...Settings` パターンに合わせてLlama設定正規化へ組み込む。

6. **インターフェース設計**
- AppSettings 追加:
  - `public string LlamaSelectedModelFileName { get; set; } = "qwen3-1_7b-instruct-q4_k_m.gguf";`
- UI:
  - `MainWindow.xaml` に `ComboBox x:Name="LlamaModelBox"` と `Refresh` ボタンを追加。
  - 候補が空のときは「No .gguf found」を表示し、Llama有効化時はエラー/警告導線を出す。
- サービスAPI（案）:
  - `IReadOnlyList<string> GetAvailableModelFileNames()`
  - `string ResolveSelectedOrDefault(string? selectedFileName)`
- バリデーション:
  - `selectedFileName` は `Path.GetFileName` で正規化し、`..` や区切り文字を拒否。
  - 実パスは必ず固定 `Models` 配下に解決（`SECURITY`）。

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に `LlamaSelectedModelFileName` を追加し、既定値を設定。
- Step 2: `LlamaModelCatalog`（新規）を追加し、`Models` の `.gguf` 一覧取得を実装。
- Step 3: `MainWindow.xaml` に Llama モデル選択 `ComboBox` と再読込UIを追加。
- Step 4: `MainWindow.xaml.cs` で候補読込/表示/保存/正規化を実装。
- Step 5: `LlamaGrpcHost` で選択モデル解決ロジックを実装し、`--model` を設定由来へ変更。
- Step 6: `model_manifest` の適用条件を見直し（既定モデルのみ自動DL対象、ユーザー選択モデルは存在確認のみ）。
- Step 7: 起動失敗時のメッセージとログを整備（選択モデル未存在・読込不可）。

8. **非機能要件チェック**
- 性能: モデル走査は設定画面表示時/明示更新時のみ実施し、定期スキャンしない。
- セキュリティ: `Models` 配下限定でパストラバーサルを防止。
- 可観測性: 起動時に「選択モデル」「実解決パス」「フォールバック有無」をログ出力。
- 互換性: 既存設定ファイルには新項目を既定値で補完し、後方互換を維持。
- 運用: モデル差し替えは `Models` に配置→UI選択→保存で完結。

9. **リスクと緩和策**
- Risk: 選択モデルが削除/移動されて起動失敗する。
- Mitigation: 既定モデルへの自動フォールバック + UI通知 + ログ出力。
- Risk: 巨大モデル選択でVRAM不足が発生する。
- Mitigation: 起動失敗時に原因を明示し、軽量モデル選択を促すメッセージを出す。
- Risk: manifest運用とユーザー選択運用が競合する。
- Mitigation: 「既定モデルのみmanifest管理」「選択モデルは存在チェックのみ」に責務分離。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs` — Llama選択モデル設定を追加。
- `MainWindow.xaml` — Llamaモデル選択UI追加。
- `MainWindow.xaml.cs` — 候補読込・選択保存・検証。
- `Services/LlamaGrpcHost.cs` — モデル解決/起動引数/manifest適用条件。
- `Services/`（新規）`LlamaModelCatalog.cs` — `.gguf` 列挙ロジック。

11. **Definition of Done**
- `Models` 内 `.gguf` がUIに列挙され、選択状態が保存/復元される。
- 起動ログに選択モデル名と実解決モデルパスが出る。
- 選択モデルが無効な場合、既定モデルへフォールバックしアプリが継続動作する。
- 既定モデル選択時は従来のmanifestベース自動DL/検証が維持される。
- モデル選択機能追加後も既存のLlama ON/OFF、起動/停止、翻訳処理が回帰しない。
