# OneOCR recovery checks

Run from the repository root on Windows:

```powershell
dotnet run --project Tools/OneOcrRepairTests/OneOcrRepairTests.csproj
```

The console runner checks staged replacement, rollback (including a blocked
rollback), missing/locked files, cancellation, helper startup/inference failures,
timeouts, empty recognition, maintenance exclusion, and duplicate-prompt and
automatic-retry suppression. It uses an isolated temporary directory and a fake
named-pipe helper; the inference-timeout check takes 30 seconds.

To also repair deliberately corrupted temporary assets using the registered
Snipping Tool and run real OCR, append `-- --native`. This requires the built
`Native/OneOcrHelper/bin/OneOcrHelper.exe` and an installed Snipping Tool with
OneOCR assets. Existing vendor files and user settings are not modified.

The repair dialog itself still needs manual visual verification in the app.
