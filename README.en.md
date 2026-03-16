# Hotkey Translator

[日本語](./README.md)

Hotkey Translator is a Windows desktop application that captures on-screen text with hotkeys, runs OCR and translation, and renders the result as an overlay.  
In addition to standard capture flows based on the active window or ROI, it also includes experimental Graphics Hook and mirror fullscreen flows.

## Key Features

- Hotkey-driven OCR / translation workflow
- ROI selection, ROI preset switching, and capture-window locking
- Standard capture via GDI / WGC / DXGI
- Graphics Hook pipeline for DX9 / DX11 / Vulkan
- Switchable OCR engines: WinRT / PaddleOCR / PaddleOCR-VL / NDLOCR-Lite / VisionLLM
- Switchable translation engines: DeepL / Google Web / Gemini / Llama.cpp
- Overlay rendering, scene-change detection, auto-translate, and mirror fullscreen

## Requirements

- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 or MSVC Build Tools + CMake
- `uv`  
  Required when using Python-based OCR, gRPC, or Llama helper services.
- `llama-server.exe`  
  Required when using local translation or VisionLLM OCR.
- `Tools/Magpie/Magpie.Core.exe`  
  Required when using mirror fullscreen.

The current WPF application targets `net8.0-windows10.0.22621.0` in [`Hotkey-Translator.csproj`](./Hotkey-Translator.csproj).

## Build

Main application:

```powershell
dotnet build .\Hotkey-Translator.sln -c Release
```

Native hook components:

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release
```

Build x86 targets as needed:

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
4. Run OCR / translation with hotkeys and review the result in the overlay.

If `uv`, `llama-server.exe`, `Magpie.Core.exe`, or native hook binaries are missing, only the related features are unavailable. The app can still be used with standard capture and a reduced feature set.

## Repository Layout

- [`MainWindow.xaml`](./MainWindow.xaml), [`MainWindow.xaml.cs`](./MainWindow.xaml.cs)  
  Main WPF frontend and application control flow
- [`Services/`](./Services)  
  Capture, OCR, translation, settings, and application services
- [`UI/`](./UI)  
  Settings UI and related controls
- [`Native/`](./Native)  
  HookHost and Graphics Hook agents
- [`OcrService/`](./OcrService), [`OcrServiceNDL/`](./OcrServiceNDL), [`OcrServiceVL/`](./OcrServiceVL), [`OcrServiceVisionLlm/`](./OcrServiceVisionLlm)  
  Python-based OCR services
- [`TranslationServiceLlama/`](./TranslationServiceLlama)  
  Local translation service integrated with Llama.cpp

## Third-Party Components

This repository includes third-party components that are not original work of this project. When publishing or redistributing this repository, keep all original copyright notices and license terms intact.

- Mirror fullscreen integration uses a modified build of Magpie published by the LunaTranslator author.  
  Hotkey Translator only integrates with and controls that component; it does not replace the original authorship or attribution of the Magpie-derived binaries.
- [`Native/ThirdParty/imgui`](./Native/ThirdParty/imgui) contains Dear ImGui.  
  The bundled copy is under the MIT License.
- [`Native/ThirdParty/MinHook`](./Native/ThirdParty/MinHook) contains MinHook.  
  The bundled copy is under the BSD 2-Clause License.

If you distribute binaries, also verify the redistribution terms for the modified Magpie binaries, model files, and any OCR / inference runtimes used by your package.

## License

The current working tree does not include a root-level `LICENSE` file that defines the license for the project as a whole. Before publishing this repository on GitHub, you should add and clearly state the project license.  
Third-party components remain subject to their own individual license terms.
