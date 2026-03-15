@echo off
setlocal

rem DX11 hook diagnostic launcher template.
rem WHY: pass-through and perf trace are mutually exclusive for analysis,
rem so keep everything opt-in and enable only the profile you want to test.

rem ===== Profile A: baseline =====
rem Leave all HT_HOOK_* variables unset.

rem ===== Profile B: pure pass-through =====
rem set HT_HOOK_PASS_THROUGH=1

rem ===== Profile C: perf trace to Temp file =====
set HT_HOOK_PERF_TRACE=1
set HT_HOOK_PERF_FILE=1

rem ===== Optional DX11 toggles =====
rem set HT_HOOK_DISABLE_PRESENT_DEBUG=1
rem set HT_HOOK_DISABLE_CAPTURE=1
rem set HT_HOOK_DISABLE_OVL_REFRESH=1
rem set HT_HOOK_DISABLE_DRAW=1
rem set HT_HOOK_DISABLE_STATUS=1

rem ===== Delayed readback diagnostics =====
rem set HT_HOOK_DISABLE_DELAYED_READBACK=1
rem set HT_HOOK_CAPTURE_RING_SIZE=3

"G:\APP Local\Hotkey-Translator\bin\Debug\net8.0-windows10.0.22621.0\Hotkey-Translator.exe" --hook-launch -- %*
