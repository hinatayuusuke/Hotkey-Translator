// WHY: Test the production message and file sink paths without injecting another process.
#define wmain HookHostMainForTest
#include "../main.cpp"
#undef wmain

#include <cstdlib>

void Require(bool condition, const char* message)
{
    if (!condition)
    {
        std::fprintf(stderr, "FAIL: %s\n", message);
        std::exit(1);
    }
}

#include "../../HookCommon/tests/DiagnosticTestFiles.h"

int main()
{
    DiagnosticTestFiles files;
    const auto path = files.LogPath(L"hook_host_");
    LogHost("event=startup_default_off.");
    Require(!std::filesystem::exists(path), "startup defaults to no file");
    for (unsigned flags = 0; flags < 4; ++flags)
    {
        const bool enabled = (flags & ht::hook::ipc::kConfigFlagEnableDiagFileSink) != 0;
        const auto before = DiagnosticTestFiles::Read(path);
        HandleMessage(INVALID_HANDLE_VALUE, "{\"type\":\"diagnostics\",\"payload\":{\"configFlags\":" + std::to_string(flags) + "}}");
        LogHost("event=matrix flags=%u.", flags);
        if (!enabled) Require(!std::filesystem::exists(path), "OFF does not create a file regardless of perf flag");
        SetDiagFileSinkEnabled(false);
        const auto after = DiagnosticTestFiles::Read(path);
        Require(enabled ? after.size() > before.size() : after == before, "Host file output follows file flag only");
        HandleMessage(INVALID_HANDLE_VALUE, "{\"type\":\"diagnostics\",\"payload\":{\"configFlags\":0}}");
        LogHost("event=after_off.");
        Require(g_diagFileHandle == INVALID_HANDLE_VALUE, "OFF closes file and does not reopen");
        Require(DiagnosticTestFiles::Read(path) == after, "OFF retains existing file without appending");
    }
    HandleMessage(INVALID_HANDLE_VALUE, "{\"type\":\"shutdown\"}");
    Require(g_diagFileHandle == INVALID_HANDLE_VALUE, "shutdown while OFF does not open a log");
    std::puts("PASS: Host diagnostic matrix, runtime toggles and shutdown");
}
