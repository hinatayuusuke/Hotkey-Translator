#include <windows.h>

#include <iostream>
#include <string>

namespace
{
    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\hotkey_translator_hook)";
    constexpr DWORD kBufferBytes = 16 * 1024;

    bool WriteResponse(HANDLE pipe, const std::string& response)
    {
        DWORD written = 0;
        return WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr) == TRUE;
    }

    std::string BuildState(const std::string& state, const std::string& reason)
    {
        return "{\"type\":\"hookState\",\"payload\":{\"state\":\"" + state + "\",\"reason\":\"" + reason +
               "\",\"api\":\"DX11\"}}\n";
    }

    void HandleMessage(HANDLE pipe, const std::string& message)
    {
        if (message.find("\"type\":\"attach\"") != std::string::npos)
        {
            // WHY: v1 host intentionally returns deterministic success while injection is implemented in later steps.
            WriteResponse(pipe, BuildState("Attached", "stub_attach_ack"));
            return;
        }

        if (message.find("\"type\":\"detach\"") != std::string::npos)
        {
            WriteResponse(pipe, BuildState("Detached", "stub_detach_ack"));
            return;
        }

        if (message.find("\"type\":\"overlayUpdate\"") != std::string::npos)
        {
            WriteResponse(pipe, BuildState("Running", "stub_overlay_ack"));
            return;
        }

        WriteResponse(pipe, BuildState("Running", "stub_unknown_message"));
    }
}

int wmain()
{
    std::wcout << L"[HookHost] starting pipe server: " << kPipeName << std::endl;

    for (;;)
    {
        HANDLE pipe = CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            kBufferBytes,
            kBufferBytes,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            std::cerr << "[HookHost] CreateNamedPipeW failed: " << GetLastError() << std::endl;
            return 1;
        }

        const BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected)
        {
            CloseHandle(pipe);
            continue;
        }

        std::string pending;
        pending.reserve(kBufferBytes);
        char buffer[kBufferBytes];
        for (;;)
        {
            DWORD bytesRead = 0;
            const BOOL ok = ReadFile(pipe, buffer, sizeof(buffer), &bytesRead, nullptr);
            if (!ok || bytesRead == 0)
            {
                break;
            }

            pending.append(buffer, buffer + bytesRead);
            std::size_t lineEnd = std::string::npos;
            while ((lineEnd = pending.find('\n')) != std::string::npos)
            {
                std::string line = pending.substr(0, lineEnd);
                pending.erase(0, lineEnd + 1);
                if (!line.empty())
                {
                    HandleMessage(pipe, line);
                }
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}
