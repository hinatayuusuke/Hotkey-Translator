# Hotkey Translator

[English](./README.en.md)

Hotkey Translator は、画面上のテキストをホットキーで取得し、OCR と翻訳を行ってオーバーレイ表示する Windows デスクトップアプリです。  
通常のアクティブウィンドウ / ROI ベースのキャプチャに加え、Graphics Hook と mirror fullscreen の実験的なフローにも対応しています。

## 主な機能

- ホットキー駆動の OCR / 翻訳ワークフロー
- ROI 選択、ROI プリセット切り替え、キャプチャウィンドウのロック
- GDI / WGC / DXGI を使った通常キャプチャ
- DX9 / DX11 / Vulkan 向け Graphics Hook パイプライン
- WinRT / OneOCR / PaddleOCR / PaddleOCR-VL / NDLOCR-Lite / VisionLLM の OCR 切り替え
- DeepL / Google Web / Gemini / Llama.cpp の翻訳切り替え
- オーバーレイ表示、シーン変化検出、自動翻訳、mirror fullscreen

## 配布版に含まれるもの

`build-dist.ps1` で作る online distribution には、次の実行要素が含まれます。

- `Hotkey-Translator.exe`
- `Tools\uv\uv.exe`
- `TranslationServiceLlama\LlamaCpp\` 配下の llama.cpp ランタイム
- `Tools\Magpie\`
- `Native\HookHost\bin\` の x64 / x86 HookHost と Hook agent
- `Native\OneOcrHelper\bin\OneOcrHelper.exe`
- OCR / 翻訳用の Python サービスソース

そのため、配布版を使うだけなら `uv` や `llama-server.exe` を別途手動配置する前提ではありません。  
ただし、次のものは配布版には含まれません。

- GGUF / mmproj モデルファイル
- OneOCR vendor ファイル
  - `oneocr.dll`
  - `oneocr.onemodel`
  - `onnxruntime.dll`
- Paddle 系の managed model payload
- 各 Python サービスの `.venv`

初回実行時の挙動:

- Python ホストは必要に応じて `uv sync` でローカル `.venv` を作成します
- Llama / VisionLLM の既定モデルは `model_manifest.json` に基づいてダウンロードされます
- Paddle / PaddleOCR-VL の managed model は実行時に取得されます

## ソースから動かす場合の前提

- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 もしくは MSVC Build Tools + CMake

ソースツリーからそのまま動かす場合は、利用する機能に応じて次も必要です。

- `Tools\uv\uv.exe` もしくは互換のある `uv`
- `TranslationServiceLlama\LlamaCpp\` の llama.cpp ランタイム
- `Tools\Magpie\Magpie.Core.exe`（mirror fullscreen を使う場合）
- OneOCR vendor ファイル（OneOCR を使う場合）

現在の WPF アプリ本体は [`Hotkey-Translator.csproj`](./Hotkey-Translator.csproj) で `net8.0-windows10.0.22621.0` をターゲットにしています。

## ビルド

アプリ本体:

```powershell
dotnet build .\Hotkey-Translator.sln -c Release
```

Native hook / helper:

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release
```

x86 Hook ターゲットが必要な場合:

```powershell
cmake -S Native -B Native/build_x86 -A Win32
cmake --build Native/build_x86 --config Release --target HookHost
cmake --build Native/build_x86 --config Release --target HookAgentDx9
cmake --build Native/build_x86 --config Release --target HookAgentDx11
cmake --build Native/build_x86 --config Release --target HookAgentVulkan
```

## 使い始め

1. アプリを起動します。
2. キャプチャ方式、OCR エンジン、翻訳エンジンを設定します。
3. 必要なら ROI を選択します。
4. ホットキーで OCR / 翻訳を実行し、オーバーレイ結果を確認します。

配布版では `uv` と llama.cpp ランタイムは同梱済みです。  
一方で、モデルファイルや OneOCR vendor ファイルが未配置の場合は、その機能だけが利用できません。

## リポジトリ構成

- [`MainWindow.xaml`](./MainWindow.xaml), [`MainWindow.xaml.cs`](./MainWindow.xaml.cs)  
  WPF フロントエンドとメイン制御
- [`Services/`](./Services)  
  キャプチャ、OCR、翻訳、設定、アプリケーションサービス
- [`UI/`](./UI)  
  設定 UI と関連コントロール
- [`Native/`](./Native)  
  HookHost、Graphics Hook agent、OneOCR helper
- [`OcrService/`](./OcrService), [`OcrServiceNDL/`](./OcrServiceNDL), [`OcrServiceVL/`](./OcrServiceVL), [`OcrServiceVisionLlm/`](./OcrServiceVisionLlm)  
  Python ベース OCR サービス
- [`TranslationServiceLlama/`](./TranslationServiceLlama)  
  Llama.cpp 連携のローカル翻訳サービス

## サードパーティについて

このリポジトリには、プロジェクト独自実装ではないサードパーティコンポーネントが含まれます。公開や再配布を行う場合は、元の著作権表示とライセンス条件を保持してください。

- mirror fullscreen 連携には、LunaTranslator 作者が公開した改造版 Magpie を利用しています。  
  Hotkey Translator はその統合と制御を行うものであり、Magpie 系コンポーネント自体の著作権や原著作者表記を置き換えるものではありません。
- [`Tools/uv/uv.exe`](./Tools/uv/uv.exe) には Astral の `uv` を含みます。  
  配布時は upstream のライセンスと notice を確認してください。
- [`TranslationServiceLlama/LlamaCpp/`](./TranslationServiceLlama/LlamaCpp) には llama.cpp runtime バイナリを含みます。  
  配布時は upstream ライセンスに加え、同梱または別配布するモデルファイルの再配布条件も確認してください。
- [`Native/ThirdParty/imgui`](./Native/ThirdParty/imgui) には Dear ImGui を含みます。  
  同梱ライセンスは MIT です。
- [`Native/ThirdParty/MinHook`](./Native/ThirdParty/MinHook) には MinHook を含みます。  
  同梱ライセンスは BSD 2-Clause です。

公開バイナリを配布する場合は、Magpie 改造版バイナリ、モデルファイル、各 OCR / 推論ランタイム、OneOCR vendor ファイルの扱いについても再配布条件を別途確認してください。

## ライセンス

このプロジェクト本体は [MIT License](./LICENSE) です。  
サードパーティコンポーネントは、それぞれ個別のライセンス条件に従います。
