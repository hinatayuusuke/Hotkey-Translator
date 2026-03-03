# Native Hook Scaffolding

This directory contains the first-stage DX11 hook scaffolding described in
`Doc/GraphicsHook_DX11_Detailed_Implementation_Plan.md`.

## Current status

- `HookHost` implements a minimal named-pipe control endpoint (`hotkey_translator_hook`).
- `HookAgentDx11` exports install/uninstall entry points with a safe stub.
- Present interception and injection internals are intentionally deferred to follow-up steps.

## Build (CMake, x64)

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release
```

## Notes

- The C# app keeps `EnableGraphicsHookPipeline=false` by default.
- If HookHost binary is missing, the app logs and stays on legacy capture.

