@echo off
setlocal

rem Case: pure pass-through
set HT_HOOK_PASS_THROUGH=1

rem Case: perf trace
set HT_HOOK_PERF_TRACE=1

rem Optional toggles
rem set HT_HOOK_DISABLE_PRESENT_DEBUG=1
rem set HT_HOOK_DISABLE_CAPTURE=1
rem set HT_HOOK_DISABLE_OVL_REFRESH=1
rem set HT_HOOK_DISABLE_DRAW=1
rem set HT_HOOK_DISABLE_STATUS=1

"G:\APP Local\Hotkey-Translator\bin\Debug\net8.0-windows10.0.22621.0\Hotkey-Translator.exe" --hook-launch -- %*
