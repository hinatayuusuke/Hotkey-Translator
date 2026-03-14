# Portable Distribution Path Requirements

このドキュメントは、**コード修正なし**で Hotkey-Translator をポータブル配布する場合の推奨フォルダ構成と設定値をまとめたものです。

## 前提
- WPF本体は単体化（1ファイル化）しても、OCR/翻訳サーバ、Hook、Magpie は外部ファイルが必要です。
- `settings.json` の保存先は実装上、`%AppData%\Hotkey-Translator\settings.json` です（EXE同階層ではありません）。

## 推奨フォルダ構成（フル機能）
```text
<PortableRoot>/
  Hotkey-Translator.exe   # WPF本体（単体化）
  Native/
    HookHost/
      bin/
        HookHost.exe
        HookAgentDx11.dll
        HookAgentDx9.dll
        HookAgentVulkan.dll
        x86/
          HookHost.exe
          HookAgentDx11.dll
          HookAgentDx9.dll
          HookAgentVulkan.dll
  Tools/
    Magpie/
      Magpie.Core.exe
      config.json
      effects/            # 実運用では同梱推奨（プロファイル依存）
  OcrService/
    server.py
    pyproject.toml
    uv.lock
    .venv/                # 事前構築推奨
  OcrServiceVL/
    server.py
    pyproject.toml
    uv.lock
    .venv/
  OcrServiceNDL/
    server.py
    pyproject.toml
    uv.lock
    model/
    config/
    .venv/
  TranslationServiceLlama/
    server.py
    pyproject.toml
    uv.lock
    model_manifest.json
    .venv/
    LlamaCpp/
      llama-server.exe
      llama.dll
      ggml.dll
      ggml-base.dll
      ggml-cpu.dll
      ggml-cuda.dll
      mtmd.dll
      Models/
        <selected-model>.gguf
```

## settings.json 推奨値（相対固定）
- HookHost / Agent は runtime で target bitness に応じて `bin` と `bin\\x86` を切り替える前提です。
- `MagpieCorePath = Tools\\Magpie\\Magpie.Core.exe`
- `PaddleGrpcProjectDir = OcrService`
- `PaddleVlGrpcProjectDir = OcrServiceVL`
- `NdlGrpcProjectDir = OcrServiceNDL`
- `LlamaGrpcProjectDir = TranslationServiceLlama`
- `LlamaSelectedModelFileName = <Models 配下の .gguf ファイル名のみ>`

## 運用上の重要注意
- `MagpieCorePath` は `Path.GetFullPath(...)` で解決されるため、起動時のカレントディレクトリが配布ルートになるようにしてください。
  - ショートカット配布時は「作業フォルダ (Start in)」を配布ルートに設定することを推奨。
- `uv` は既定値が `"uv"` なので、`PATH` に `uv` が必要です。
  - 未導入環境では、各 `*GrpcUvPath` に同梱 `uv.exe` のパスを設定してください。
- 現状の Llama 実装は CUDA DLL の存在チェックがあります。
  - CPU-only 運用でも `TranslationServiceLlama\\.venv\\Lib\\site-packages\\nvidia\\...\\bin` が必要です。

## 参照実装（確認済み）
- OCR gRPC 経路:
  - `Services/OcrEngine.cs`
  - `Services/PaddleGrpcHost.cs`
  - `Services/PaddleVlGrpcHost.cs`
  - `Services/NdlGrpcHost.cs`
- Llama 起動・固定相対パス・DLLチェック:
  - `Services/LlamaGrpcHost.cs`
- HookHost 起動:
  - `Services/Hook/Dx11HookClientService.cs`
- Magpie Core / config 解決:
  - `Services/Application/MagpieSessionController.cs`
- 設定保存先:
  - `Services/SettingsService.cs`


$bin = "G:\APP Local\Hotkey-Translator\bin\Debug\net8.0-windows10.0.22621.0"
cd $bin
cmd /c mklink /J Tools "G:\APP Local\Hotkey-Translator\Tools"
cmd /c mklink /J Native "G:\APP Local\Hotkey-Translator\Native"
cmd /c mklink /J OcrService "G:\APP Local\Hotkey-Translator\OcrService"
cmd /c mklink /J OcrServiceVL "G:\APP Local\Hotkey-Translator\OcrServiceVL"
cmd /c mklink /J OcrServiceNDL "G:\APP Local\Hotkey-Translator\OcrServiceNDL"
cmd /c mklink /J TranslationServiceLlama "G:\APP Local\Hotkey-Translator\TranslationServiceLlama"
