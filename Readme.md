# Hotkey Translator

[English](./README.en.md)

Hotkey Translator は、Windows 上の画面テキストをホットキーで取り込み、OCR と翻訳を実行してオーバーレイ表示するためのデスクトップアプリです。  
アクティブウィンドウや ROI を対象にした通常キャプチャに加え、実験的な Graphics Hook と mirror fullscreen フローも扱えます。

## 主な機能

- ホットキー主体の OCR / 翻訳実行
- ROI 選択、ROI プリセット切り替え、対象ウィンドウのロック
- GDI / WGC / DXGI を使った通常キャプチャ
- DX9 / DX11 / Vulkan 向け Graphics Hook パイプライン
- WinRT / PaddleOCR / PaddleOCR-VL / NDLOCR-Lite / VisionLLM の OCR 切り替え
- DeepL / Google Web / Gemini / Llama.cpp の翻訳切り替え
- オーバーレイ表示、シーン変化監視、自動翻訳、mirror fullscreen

## 動作環境

- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 もしくは MSVC Build Tools + CMake
- `uv`  
  Python ベースの OCR / gRPC / Llama 補助サービスを使う場合に必要です。
- `llama-server.exe`  
  ローカル翻訳または VisionLLM OCR を使う場合に必要です。
- `Tools/Magpie/Magpie.Core.exe`  
  mirror fullscreen を使う場合に必要です。

現在の WPF アプリ本体は [`Hotkey-Translator.csproj`](./Hotkey-Translator.csproj) で `net8.0-windows10.0.22621.0` をターゲットにしています。

## ビルド

アプリ本体:

```powershell
dotnet build .\Hotkey-Translator.sln -c Release
```

ネイティブ Hook 一式:

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release
```

必要に応じて x86 側も個別にビルドします。

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
4. ホットキーで OCR / 翻訳を実行し、オーバーレイで結果を確認します。

`uv`、`llama-server.exe`、`Magpie.Core.exe`、Native Hook バイナリが見つからない場合、その機能だけが利用できません。通常キャプチャと一部機能だけで使う構成も可能です。

## リポジトリ構成

- [`MainWindow.xaml`](./MainWindow.xaml), [`MainWindow.xaml.cs`](./MainWindow.xaml.cs)  
  WPF フロントエンドとメイン制御
- [`Services/`](./Services)  
  キャプチャ、OCR、翻訳、設定、アプリ制御
- [`UI/`](./UI)  
  設定 UI と各種コントロール
- [`Native/`](./Native)  
  HookHost と各 Graphics Hook エージェント
- [`OcrService/`](./OcrService), [`OcrServiceNDL/`](./OcrServiceNDL), [`OcrServiceVL/`](./OcrServiceVL), [`OcrServiceVisionLlm/`](./OcrServiceVisionLlm)  
  Python ベース OCR サービス
- [`TranslationServiceLlama/`](./TranslationServiceLlama)  
  Llama.cpp 連携のローカル翻訳サービス

## サードパーティについて

このリポジトリには、自作ではないサードパーティコンポーネントが含まれます。公開時には、各コンポーネントの著作権表示とライセンス条件を必ず維持してください。

- mirror fullscreen 連携には、LunaTranslator 作者が公開した改造版 Magpie を利用しています。  
  Hotkey Translator はその統合と制御を行うものであり、Magpie 系コンポーネント自体の著作権や原著作者表記を置き換えるものではありません。
- [`Native/ThirdParty/imgui`](./Native/ThirdParty/imgui) には Dear ImGui を含みます。  
  リポジトリ同梱分のライセンスは MIT です。
- [`Native/ThirdParty/MinHook`](./Native/ThirdParty/MinHook) には MinHook を含みます。  
  リポジトリ同梱分のライセンスは BSD 2-Clause です。

公開バイナリを配布する場合は、Magpie 改造版バイナリ、モデルファイル、各 OCR / 推論ランタイムについても再配布条件を別途確認してください。

## ライセンス

現時点の作業ツリーには、このプロジェクト全体を定義するルート `LICENSE` ファイルがありません。GitHub で公開する前に、リポジトリ本体のライセンスを別途明示することを推奨します。  
サードパーティコンポーネントは、それぞれ個別のライセンス条件に従います。
