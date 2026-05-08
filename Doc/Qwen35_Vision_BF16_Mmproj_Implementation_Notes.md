# Qwen3.5 Vision BF16 projector 実装注意メモ

## 1. 目的
- Qwen3.5 系を `llama.cpp` で画像認識に使うときは、量子化済みの本体 GGUF だけでは足りず、vision projector 用の BF16 `mmproj` も必要になる。
- このメモは、「なぜ BF16 projector が必要か」よりも、「実装時にどこでハマるか」を短く確認するための注意メモである。

## 2. 先に結論
- `Qwen3.5-4B-Q4_K_M.gguf` のような本体 model と、`mmproj-Qwen3.5-4B-BF16.gguf` のような projector は別ファイルとして扱う。
- `llama-server` 起動時は `-m <model>` に加えて `--mmproj <bf16 projector>` を必ず渡す。
- upstream の配布実ファイル名は `mmproj-BF16.gguf` でも、アプリ内では `mmproj-Qwen3.5-4B-BF16.gguf` のような model 別 alias 名で保存した方が安全。
- 4B と 9B は upstream 側で同じ `mmproj-BF16.gguf` 名を使うことがあるので、フラットな `Models` ディレクトリへそのまま保存すると衝突しやすい。

## 3. このリポジトリでの現行扱い
### 3.1 既定ペア
- Vision model: `Qwen3.5-4B-Q4_K_M.gguf`
- Vision mmproj: `mmproj-Qwen3.5-4B-BF16.gguf`

### 3.2 実ファイル名と alias 名
- upstream download URL:
  - model: `Qwen3.5-4B-Q4_K_M.gguf`
  - mmproj: `mmproj-BF16.gguf`
- local 保存名:
  - model: `Qwen3.5-4B-Q4_K_M.gguf`
  - mmproj: `mmproj-Qwen3.5-4B-BF16.gguf`

重要なのは、`mmproj-Qwen3.5-4B-BF16.gguf` は upstream 実ファイル名ではなく、ローカル alias 名だという点である。

## 4. 実装上の注意点
### 4.1 model と mmproj を別設定にする
- Vision model 選択値と mmproj 選択値を 1 つの文字列にまとめない。
- 本体 GGUF が存在しても mmproj が欠けているケースは実際に起こるので、設定・UI・起動前検証は別々に持つ。

このリポジトリでも:
- `VisionLlmSelectedModelFileName`
- `VisionLlmSelectedMmprojFileName`

に分けている。

### 4.2 起動コマンドで mmproj を必須扱いにする
- `llama-server` を起動する層で `--mmproj` を optional にしない。
- Vision model を使う限り、projector 抜きでの起動成功を期待しない方がよい。

現行実装でも Python 側は以下の形で必須になっている。

```text
llama-server.exe -m <model.gguf> --mmproj <mmproj.gguf>
```

### 4.3 generic 名をそのまま設定値にしない
- `mmproj-BF16.gguf`
- `mmproj-F16.gguf`
- `4Bmmproj-F16.gguf`

のような generic 名は、あとで 4B/9B を混在させたときに対応関係が崩れやすい。

設定値として保存するなら、少なくとも model 系列が読める alias 名に寄せた方がよい。

推奨:
- `mmproj-Qwen3.5-4B-BF16.gguf`
- `mmproj-Qwen3.5-9B-BF16.gguf`

### 4.4 upstream 実ファイル名と local 保存名を分ける
- ダウンロード元が常に model 別ファイル名を持つとは限らない。
- `model_manifest.json` や downloader DTO では、少なくとも以下を分ける。

```json
{
  "download_url": ".../mmproj-BF16.gguf?download=true",
  "local_filename": "mmproj-Qwen3.5-4B-BF16.gguf"
}
```

これを分けないと、4B と 9B の projector が同じ保存名へ落ちて上書きされる。

### 4.5 起動前検証は model と mmproj の両方で行う
- file exists だけでなく、できれば SHA256 と size も見る。
- auto-download を入れる場合も、本体 model だけでなく mmproj まで揃って初めて ready 扱いにする。

このリポジトリの現行構成でも、Vision host 起動前に manifest ベースで model / mmproj の 2 asset を保証している。

### 4.6 UI では「missing」を見せる
- mmproj は model と独立に欠けうるので、UI で model だけ正常に見せると復旧しづらい。
- model list と mmproj list は別々に組み、欠落している選択値も `(missing)` 表示で残す方が修復しやすい。

## 5. 互換処理で気を付ける点
- 旧設定に `mmproj-BF16.gguf` や `mmproj-F16.gguf` が残っている場合、そのまま新構成へ持ち込むと 4B/9B の区別がつかない。
- そのため正規化層で旧 generic 名を model 別 alias 名へ寄せる migration を持っておくと安全。

このリポジトリでは旧名:
- `4Bmmproj-F16.gguf`
- `mmproj-F16.gguf`
- `mmproj-BF16.gguf`

を `mmproj-Qwen3.5-4B-BF16.gguf` へ寄せている。

## 6. よくある失敗
### 6.1 本体 GGUF だけ置いて満足する
- 症状: `llama-server` が起動しない、あるいは vision 入力で失敗する
- 原因: mmproj 未配置

### 6.2 4B と 9B で同じ `mmproj-BF16.gguf` を共有保存する
- 症状: どちらかの model 切替時に意図しない projector を掴む
- 原因: local filename の衝突

### 6.3 settings は alias 名、manifest は upstream 名、UI は generic 名でバラバラ
- 症状: auto-download 条件判定が外れる、missing 表示が増える、保存後に別名へ巻き戻る
- 原因: 「実ファイル名」「保存名」「表示名」の責務分離が曖昧

### 6.4 mmproj を fallback 扱いにしてしまう
- 症状: 起動時の異常があとで OCR 実行時に出る
- 原因: fail fast せず、遅延エラーにしている

## 7. 実装時の推奨ルール
1. Vision model と mmproj は別設定、別検証、別 UI にする。
2. `llama-server` 起動層では `--mmproj` を必須にする。
3. upstream 実ファイル名と local alias 名を分離する。
4. local alias 名には `Qwen3.5-4B` / `Qwen3.5-9B` のような系列名を入れる。
5. generic 旧名は正規化層で alias 名へ migration する。
6. 起動前に model と mmproj の両方を保証し、欠けていれば fail fast にする。

## 8. このプロジェクトで参照すべき箇所
- `OcrServiceVisionLlm/model_manifest.json`
  - BF16 projector の download URL と local alias 名の分離例
- `Services/Settings/SettingsHostNormalizer.cs`
  - generic 旧名を alias 名へ寄せる互換処理
- `Services/VisionLlmGrpcHost.cs`
  - 起動前の model / mmproj 検証
- `MainWindow.xaml.cs`
  - model / mmproj を別リストとして扱い、missing 状態を UI に残す処理
- `OcrServiceVisionLlm/test_vision_llama_engine.py`
  - `--mmproj` を含めた単体実行例

## 9. ひとことで言うと
- Qwen3.5 Vision を `llama.cpp` で扱うとき、BF16 projector は「補助ファイル」ではなく、実質的に model ペアの片割れである。
- 実装は `model + mmproj` の 2 ファイルを 1 組として扱う前提で組んだ方が、あとで壊れにくい。
