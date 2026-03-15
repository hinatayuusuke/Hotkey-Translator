# Native Hook Scaffolding

This directory contains the first-stage DX11 hook scaffolding described in
`Doc/GraphicsHook_DX11_Detailed_Implementation_Plan.md`.

## Current status

- `HookHost` implements a minimal named-pipe control endpoint (`hotkey_translator_hook`).
- `HookAgentDx11` exports install/uninstall entry points with a safe stub.
- Present interception and injection internals are intentionally deferred to follow-up steps.

## Build (CMake)

Configure once for x64:

```powershell
cmake -S Native -B Native/build -A x64
```

Build all native targets:

```powershell
cmake --build Native/build --config Release
```

Build each native target individually:

```powershell
cmake --build Native/build --config Release --target HookHost
cmake --build Native/build --config Release --target HookAgentDx11
cmake --build Native/build --config Release --target HookAgentDx9
cmake --build Native/build --config Release --target HookAgentVulkan
```

Build the DX9 hook for x86:

```powershell
cmake -S Native -B Native/build_x86 -A Win32
cmake --build Native/build_x86 --config Release --target HookHost
cmake --build Native/build_x86 --config Release --target HookAgentDx9
```

## Notes

- The C# app keeps `EnableGraphicsHookPipeline=false` by default.
- If HookHost binary is missing, the app logs and stays on legacy capture.
- `HookAgentVulkan` is only generated when the Vulkan SDK is installed and `find_package(Vulkan)` succeeds.

