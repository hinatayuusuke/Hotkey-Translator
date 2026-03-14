# VisionLLM Model Auto Download Implementation Plan

1. **概要（1-3行）**

VisionLLM 起動時に、必要な GGUF モデルと mmproj が未配置なら自動で取得する。  
既存の `LlamaGrpcHost` にある manifest ベースのダウンロード実装を横展開し、VisionLLM でも同じ検証・排他・原子的配置を使う。  
初回対象は `Qwen3.5-4B-Q4_K_M.gguf` と、その対応 mmproj 1 組に限定し、Hugging Face 実ファイル名とローカル保存名を分離して管理する。  

2. **ゴール / 非ゴール**

- ゴール
- VisionLLM 起動前に `model` / `mmproj` の存在と整合性を保証する。
- 未配置時は Hugging Face から自動ダウンロードし、完了後に通常どおり起動する。
- 既存の `TranslationServiceLlama` と同水準の安全性を持たせる。
- モデル既定値、UI 既定値、正規化既定値の不整合を解消する。
- 4B と 9B の mmproj が同一 `Models` フォルダで衝突せずに共存できるようにする。

- 非ゴール
- 複数の Vision モデルを自動判別して動的に切り替えること。
- 旧命名の mmproj を広く互換サポートすること。
- llama.cpp の `-hf` 起動方式へ全面移行すること。
- UI に大きなダウンロード管理画面を追加すること。

3. **前提・仮定**

- 現行 VisionLLM は `Services/VisionLlmGrpcHost.cs` から Python gRPC サーバを起動し、Python 側はローカル実ファイルの `--model` / `--mmproj` を必須としている。
- 既存 `LlamaGrpcHost` には manifest + SHA256 検証 + `.lock` + `.tmp` による安全な取得処理がある。
- 配布元候補の確認では、`unsloth/Qwen3.5-4B-GGUF` に `Qwen3.5-4B-Q4_K_M.gguf` と `mmproj-BF16.gguf` が存在した。
- 同じく `unsloth/Qwen3.5-9B-GGUF` にも `mmproj-BF16.gguf` があり、4B と 9B で mmproj の Hugging Face 実ファイル名が衝突する。
- ローカル `Models` ディレクトリはフラット構成なので、Hugging Face 実ファイル名をそのまま保存すると 4B と 9B が共存できない。
- したがって初回実装では、ダウンロード元の実ファイル名とローカル保存名を分離し、ローカルでは `mmproj-Qwen3.5-4B-BF16.gguf` のような alias 名で保存する前提にする。
- 要求名の `mmproj-Qwen3.5-4B-BF16.gguf` は、確認した主要公開 repo では完全一致を確認できなかった。これはローカル alias 名として使う。

4. **現状整理**

- `Services/VisionLlmGrpcHost.cs`
- `StartProcessCoreAsync(...)` で `model` / `mmproj` の存在を直接確認し、未配置なら即例外で終了する。
- `OnBeforeStartAsync(...)` は未 override で、起動前準備処理を入れる余地が残っている。

- `OcrServiceVisionLlm/server.py`
- `--model` と `--mmproj` を必須引数にしており、`resolve_path(...)` でローカルファイル存在を前提にしている。

- `Services/LlamaGrpcHost.cs`
- `EnsureLlamaModelAsync(...)` が manifest ベースの単一ファイル自動ダウンロードを実装済み。
- 既存ファイル検証、排他ロック、`.tmp` 保存、SHA256 検証、原子的 move を備えている。

- `Models/AppSettings.cs`
- VisionLLM の既定は `Qwen3.5-4B-Q4_K_M.gguf` / `4Bmmproj-F16.gguf` になっている。

- `Services/Settings/SettingsHostNormalizer.cs`
- VisionLLM の既定は `Qwen3.5-9B-Q4_K_M.gguf` / `mmproj-F16.gguf` になっている。

- `MainWindow.xaml.cs`
- UI 既定は `Qwen3.5-4B-Q4_K_M.gguf` / `4Bmmproj-F16.gguf` になっている。

- 既存ローカル配置
- すでにローカルでは `mmproj-Qwen3.5-4B-BF16.gguf` と `mmproj-Qwen3.5-9B-BF16.gguf` のようにモデル別 alias 名で共存している。

- 問題点
- 既定ファイル名が 3 箇所で揃っていない。
- mmproj の命名が配布元実ファイル名と一致していない。
- 4B と 9B の Hugging Face 実ファイル名が衝突するため、保存名を別管理しないと共存できない。
- Vision 側だけ自動取得機構がなく、初回導入時に手動配置が前提になっている。

5. **提案アーキテクチャ**

- コンポーネント構成
- `VisionLlmGrpcHost`
  - 起動前に Vision 用モデル asset を確保する。
- `VisionLlmModelCatalog` または共通 asset resolver
  - Vision 用の安全なファイル名正規化と配置先解決を担う。
- `ModelAssetDownloader` 相当の共通処理
  - 既存 `LlamaGrpcHost` のダウンロード処理を共通化し、複数 asset を扱えるようにする。
- `OcrServiceVisionLlm/model_manifest.json`
  - 初回は `model` / `mmproj` の 2 エントリを持つ manifest を置く。
  - 各 asset に「ダウンロード元の実ファイル名」と「ローカル保存 alias 名」を持たせる。

- データフロー / シーケンス
- VisionLLM 起動時:
  1. `GrpcHostBase.StartAsync(...)` から `OnBeforeStartAsync(...)` が呼ばれる。
  2. Vision 用 manifest を読む。
  3. `model` と `mmproj` の各 asset について、manifest に定義したローカル alias 名の既存ファイルをサイズ・SHA256 で検証する。
  4. 不足または不整合なら、排他ロックを取得してダウンロードする。
  5. ダウンロード元 URL の内容を `.tmp` へ保存し、検証成功後に manifest のローカル alias 名へ move する。
  6. 全 asset 準備完了後に `StartProcessCoreAsync(...)` が通常起動する。

- 既存パターンへの整合
- `LlamaGrpcHost` と同じく「起動前準備で runtime / model を揃える」流れに合わせる。
- Vision Python 側の引数契約は維持し、ダウンロード責務は C# Host 側に閉じる。
- ローカル保存名は Hugging Face 実ファイル名ではなく alias 名を正本とし、既存の flat `Models` ディレクトリ構成を維持する。
- 自動取得の対象は manifest に明記した asset のみに限定し、過剰な互換分岐は入れない。

6. **インターフェース設計**

- 追加ファイル
- `OcrServiceVisionLlm/model_manifest.json`
  - 例:
  - `model.local_filename = "Qwen3.5-4B-Q4_K_M.gguf"`
  - `model.download_url = ".../Qwen3.5-4B-Q4_K_M.gguf?download=true"`
  - `mmproj.local_filename = "mmproj-Qwen3.5-4B-BF16.gguf"`
  - `mmproj.download_url = ".../mmproj-BF16.gguf?download=true"`
  - 各エントリに `download_url` / `local_filename` / `sha256` / `size_bytes` を持たせる。

- C# 側追加候補
- `VisionLlmGrpcHost.OnBeforeStartAsync(...)`
  - Vision 用 runtime と model asset の準備を行う。
- `EnsureVisionLlmAssetsAsync(...)`
  - Vision manifest を読み、必要 asset を順にまたは逐次保証する。
- `TryValidateAsset(...)`
  - 既存 `TryValidateModel(...)` の汎化版。
- `ModelAssetManifest`
  - 単一 asset の manifest DTO。
- `VisionModelManifest`
  - `model` / `mmproj` を束ねる DTO。

- AppSettings / 既定値
- `VisionLlmSelectedModelFileName`
  - 既定を `Qwen3.5-4B-Q4_K_M.gguf` に統一する。
- `VisionLlmSelectedMmprojFileName`
  - 既定を `mmproj-Qwen3.5-4B-BF16.gguf` に統一する。

- バリデーション
- `SECURITY:` ファイル名は bare `.gguf` のみ許可し、固定 `Models` ディレクトリ配下に閉じ込める。
- manifest の `local_filename` と選択済みファイル名が一致しない場合は fail fast にする。
- WHY: Hugging Face 側の実ファイル名は 4B / 9B で衝突しうるため、アプリ内部ではローカル alias 名を正本にする。
- ユーザーが既定以外の Vision モデル名を選んだ場合は、自動ダウンロードせず、明示的にローカル配置を要求する。

7. **実装手順（ステップ分割）**

- Step 1
- VisionLLM の既定ファイル名を 1 系統に揃える。
- 対象:
- `Models/AppSettings.cs`
- `Services/Settings/SettingsHostNormalizer.cs`
- `MainWindow.xaml.cs`

- Step 2
- `OcrServiceVisionLlm/model_manifest.json` を追加する。
- `model` と `mmproj` の 2 asset を記述し、配布元 URL、ローカル alias 名、SHA256、サイズを固定する。

- Step 3
- `LlamaGrpcHost` の単一ファイル downloader を共通化する。
- `LlamaGrpcHost` と `VisionLlmGrpcHost` の両方から使える helper へ抽出する。
- NOTE: 実装重複を許容して Vision 側へ最小移植する案もあるが、検証ロジックの分岐事故を避けるため共通化を優先する。

- Step 4
- `VisionLlmGrpcHost.OnBeforeStartAsync(...)` を実装する。
- Vision 用 `.venv` 準備が必要なら `TranslationServiceLlama` と同様に fingerprint ベースの `uv sync` を導入する。
- その後に model / mmproj 保証処理を呼ぶ。

- Step 5
- `VisionLlmGrpcHost.StartProcessCoreAsync(...)` から重複した存在確認の一部を整理する。
- WHY: 起動前保証と起動直前検証の責務を分け、エラー原因を「取得失敗」と「起動失敗」に分離するため。

- Step 6
- ログを追加する。
- `stage=vision_model_download event=start|skip|complete|failed`
- asset 名、URL、local_filename、size、elapsed_ms を出す。
- ハッシュ不一致や再取得理由も記録する。

- Step 7
- 最低限の確認を行う。
- 既定値で未配置起動したときに自動取得されること。
- 2 回目起動は再ダウンロードされないこと。
- 破損ファイルを置いた場合に再取得されること。
- ユーザー指定の非既定モデルは自動取得されず、明示エラーになること。
- 4B と 9B の mmproj が同一フォルダで共存すること。

8. **非機能要件チェック**

- 性能
- 初回は約 3.4 GB のダウンロードが発生するため時間がかかる。
- 2 回目以降は SHA256 検証のみでスキップされる想定。

- セキュリティ
- ダウンロード先を固定ディレクトリに限定する。
- ファイル名は `Path.GetFileName(...)` 済みの `.gguf` のみ許可する。
- SHA256 検証必須とし、一致しないファイルは採用しない。

- 可観測性
- 既存 logger へ取得開始・完了・スキップ・失敗を出す。
- 将来 UI 進捗を載せる場合でも、まずはログだけで切り分け可能にする。

- 互換性
- 既定の Vision mmproj 名が `4Bmmproj-F16.gguf` から `mmproj-Qwen3.5-4B-BF16.gguf` へ変わるため、旧設定は migration または正規化で置換する必要がある。
- 既存の 9B ローカル alias と共存できるよう、Hugging Face 実ファイル名ではなく alias 名で管理する。
- COMPAT: Hugging Face 実ファイル名を UI や設定値へ露出させない。

- 運用
- オフライン環境では初回起動に失敗する。
- その場合はログに「どの URL が取得できなかったか」を明示する。

9. **リスクと緩和策**

- Risk: Hugging Face 側のファイル名や URL が変わる。
- Mitigation: manifest に URL と SHA256 を固定し、変更時は manifest 更新だけで追従できる構造にする。

- Risk: 4B と 9B の mmproj が同名で、後からダウンロードした方が上書きする。
- Mitigation: ダウンロード元 URL とローカル保存 alias 名を分離し、モデル別 alias で保持する。

- Risk: mmproj の別名運用により、manifest / 設定 / 実ファイルの対応がずれる。
- Mitigation: manifest の `local_filename` を AppSettings の既定値と一致させ、選択値との不一致は fail fast にする。

- Risk: 大容量ダウンロードで起動待ちが長くなり、ユーザーがフリーズと誤認する。
- Mitigation: ログへ開始直後に明示出力し、必要なら後続で UI 表示を追加する。

- Risk: 複数プロセス同時起動で同じファイルを壊す。
- Mitigation: `.lock` と `.tmp` を使い、既存 Llama 実装と同じ排他戦略を使う。

- Risk: 破損した部分ファイルをそのまま採用する。
- Mitigation: 完了後の SHA256 検証に失敗した場合は削除して fail fast にする。

10. **影響範囲**

- 変更ファイル候補
- `Services/VisionLlmGrpcHost.cs`
- `Services/LlamaGrpcHost.cs`
- `Services/GrpcHost/GrpcHostBase.cs`
- `Services/Settings/SettingsHostNormalizer.cs`
- `Models/AppSettings.cs`
- `MainWindow.xaml.cs`
- `OcrServiceVisionLlm/model_manifest.json`
- 必要なら共通 downloader / manifest 用の新規 C# ファイル

- 移行
- 既存設定で `VisionLlmSelectedMmprojFileName` が旧名なら、新既定 alias 名へ合わせる migration または正規化が必要。

- ドキュメント更新
- VisionLLM セットアップ手順に「初回起動で自動取得される」ことを追記する。
- オフライン利用時は事前配置が必要であることも明記する。
- mmproj は Hugging Face 実ファイル名ではなく、ローカル alias 名で管理していることを明記する。

11. **Definition of Done**

- VisionLLM 既定値が `AppSettings` / `Normalizer` / UI で一致している。
- Vision 用 manifest が追加されている。
- Vision 用 manifest がダウンロード元 URL とローカル alias 名を分離して定義している。
- 未配置状態で VisionLLM 起動すると model / mmproj が自動取得される。
- 取得後に VisionLLM gRPC が通常起動する。
- 2 回目起動で再取得が走らない。
- 破損ファイル時に再取得または明示エラーになる。
- 非既定ファイル名では自動取得せず、意図どおり fail fast する。
- 4B と 9B の mmproj が同一 `Models` フォルダで衝突せずに共存できる。
- ログだけで取得成否を判別できる。

12. **調査メモ**

- 確認した配布元候補
- `unsloth/Qwen3.5-4B-GGUF`
- `unsloth/Qwen3.5-9B-GGUF`
- `bartowski/Qwen_Qwen3.5-4B-GGUF`

- 初回推奨
- model:
- ダウンロード元: `Qwen3.5-4B-Q4_K_M.gguf`
- ローカル保存名: `Qwen3.5-4B-Q4_K_M.gguf`
- mmproj:
- ダウンロード元: `mmproj-BF16.gguf`
- ローカル保存名: `mmproj-Qwen3.5-4B-BF16.gguf`

- NOTE: `mmproj-Qwen3.5-4B-BF16.gguf` はローカル alias 名として使う。Hugging Face 側の配布実ファイル名は `mmproj-BF16.gguf` を前提にする。
