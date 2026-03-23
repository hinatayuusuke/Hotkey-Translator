# Hotkey Translator

[日本語](./README.md)

Hotkey Translator is a Windows desktop application that captures on-screen text with hotkeys, runs OCR and translation, and renders the result as an overlay.  
In addition to standard active-window / ROI-based capture, it also includes experimental Graphics Hook and mirror fullscreen flows.

## Key Features

- Hotkey-driven OCR / translation workflow
- ROI selection, ROI preset switching, and capture-window locking
- Standard capture via GDI / WGC / DXGI
- Graphics Hook pipeline for DX9 / DX11 / Vulkan
- Switchable OCR engines: WinRT / OneOCR / PaddleOCR / PaddleOCR-VL / NDLOCR-Lite / VisionLLM
- Switchable translation engines: DeepL / Google Web / Gemini / Llama.cpp
- Overlay rendering, scene-change detection, auto-translate, and mirror fullscreen

## What The Distribution Includes

The online distribution produced by `build-dist.ps1` includes these runtime components:

- `Hotkey-Translator.exe`
- `Tools\\uv\\uv.exe`
- llama.cpp runtime binaries under `TranslationServiceLlama\\LlamaCpp\\`
- `Tools\\Magpie\\`
- x64 / x86 HookHost and hook agents under `Native\\HookHost\\bin\\`
- `Native\\OneOcrHelper\\bin\\OneOcrHelper.exe`
- Python OCR / translation service sources

That means the distributed package no longer assumes the user will manually provide `uv` or `llama-server.exe` first.  
However, the following are still not bundled:

- GGUF / mmproj model payloads
- OneOCR vendor files
  - `oneocr.dll`
  - `oneocr.onemodel`
  - `onnxruntime.dll`
- Paddle managed model payloads
- Python `.venv` directories

First-run behavior:

- Python hosts create their local `.venv` with `uv sync` when needed
- Default Llama / VisionLLM models download from `model_manifest.json`
- Paddle / PaddleOCR-VL managed models download on demand

## Requirements For Running From Source

- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 or MSVC Build Tools + CMake

If you run directly from the source tree instead of the packaged distribution, you still need the relevant runtime files depending on the features you use:

- `Tools\\uv\\uv.exe` or a compatible `uv`
- llama.cpp runtime files under `TranslationServiceLlama\\LlamaCpp\\`
- `Tools\\Magpie\\Magpie.Core.exe` if you use mirror fullscreen
- OneOCR vendor files if you use OneOCR

The current WPF application targets `net8.0-windows10.0.22621.0` in [`Hotkey-Translator.csproj`](./Hotkey-Translator.csproj).

## Build

Main application:

```powershell
dotnet build .\Hotkey-Translator.sln -c Release
```

Native hook / helper components:

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release
```

Build x86 hook targets as needed:

```powershell
cmake -S Native -B Native/build_x86 -A Win32
cmake --build Native/build_x86 --config Release --target HookHost
cmake --build Native/build_x86 --config Release --target HookAgentDx9
cmake --build Native/build_x86 --config Release --target HookAgentDx11
cmake --build Native/build_x86 --config Release --target HookAgentVulkan
```

## Getting Started

1. Launch the application.
2. Configure the capture mode, OCR engine, and translation engine.
3. Select an ROI if needed.
4. Run OCR / translation with hotkeys and review the overlay result.

In the packaged distribution, `uv` and the llama.cpp runtime are already bundled.  
If model files or OneOCR vendor files are missing, only those related features remain unavailable.

## Repository Layout

- [`MainWindow.xaml`](./MainWindow.xaml), [`MainWindow.xaml.cs`](./MainWindow.xaml.cs)  
  Main WPF frontend and application control flow
- [`Services/`](./Services)  
  Capture, OCR, translation, settings, and application services
- [`UI/`](./UI)  
  Settings UI and related controls
- [`Native/`](./Native)  
  HookHost, Graphics Hook agents, and the OneOCR helper
- [`OcrService/`](./OcrService), [`OcrServiceNDL/`](./OcrServiceNDL), [`OcrServiceVL/`](./OcrServiceVL), [`OcrServiceVisionLlm/`](./OcrServiceVisionLlm)  
  Python-based OCR services
- [`TranslationServiceLlama/`](./TranslationServiceLlama)  
  Local translation service integrated with Llama.cpp

## Third-Party Components

This repository includes third-party components that are not original work of this project. When publishing or redistributing this repository, keep all original copyright notices and license terms intact.

- Mirror fullscreen integration uses a modified build of Magpie published by the LunaTranslator author.  
  Hotkey Translator only integrates with and controls that component; it does not replace the original authorship or attribution of the Magpie-derived binaries.
- [`Tools/uv/uv.exe`](./Tools/uv/uv.exe) includes Astral's `uv`.  
  When redistributing binaries, verify the upstream license and notice requirements.
- [`TranslationServiceLlama/LlamaCpp/`](./TranslationServiceLlama/LlamaCpp) includes llama.cpp runtime binaries.  
  When redistributing binaries, verify the upstream license and also the redistribution terms for any model files you bundle or expect users to provide separately.
- [`Native/ThirdParty/imgui`](./Native/ThirdParty/imgui) contains Dear ImGui.  
  The bundled copy is under the MIT License.
- [`Native/ThirdParty/MinHook`](./Native/ThirdParty/MinHook) contains MinHook.  
  The bundled copy is under the BSD 2-Clause License.

If you distribute binaries, also verify the redistribution terms for the modified Magpie binaries, model files, OCR / inference runtimes, and any OneOCR vendor files you expect users to provide separately.

## License

The project itself is licensed under the [MIT License](./LICENSE).  
Third-party components remain subject to their own individual license terms.
