#include <windows.h>

#include <cstdint>
#include <iostream>
#include <iterator>
#include <string>
#include <tlhelp32.h>
#include <unordered_map>
#include <vector>

#include <wincrypt.h>

#include "../HookCommon/HookIpcProtocol.h"
#include "../HookCommon/SharedHookConfig.h"
#include "../HookCommon/SharedOverlayCommands.h"

namespace
{
    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\hotkey_translator_hook)";
    constexpr DWORD kBufferBytes = 64 * 1024;
    constexpr DWORD kRemoteTimeoutMs = 5000;

    struct AttachRequest
    {
        DWORD pid = 0;
        bool enableOverlay = true;
        std::uint32_t captureFpsLimit = 15;
    };

    struct ProcessHookState
    {
        DWORD pid = 0;
        HMODULE remoteModule = nullptr;
        std::wstring dllPath;
        ht::hook::ipc::GraphicsApi api = ht::hook::ipc::GraphicsApi::Dx11;
        ht::hook::ipc::SharedHookConfigWriter configWriter;
        ht::hook::ipc::SharedOverlayCommandsWriter overlayWriter;
    };

    std::unordered_map<DWORD, ProcessHookState> g_states;

    bool WriteResponse(HANDLE pipe, const std::string& response)
    {
        DWORD written = 0;
        return WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr) == TRUE;
    }

    std::string JsonEscape(const std::string& s)
    {
        std::string out;
        out.reserve(s.size() + 16);
        for (const char c : s)
        {
            switch (c)
            {
                case '\\': out += "\\\\"; break;
                case '"': out += "\\\""; break;
                case '\n': out += "\\n"; break;
                case '\r': out += "\\r"; break;
                case '\t': out += "\\t"; break;
                default: out += c; break;
            }
        }
        return out;
    }

    std::string BuildState(const std::string& state, const std::string& reason, ht::hook::ipc::GraphicsApi api, DWORD pid)
    {
        const std::wstring mapNameW = ht::hook::ipc::BuildFrameMappingName(pid, api);
        // WHY: Avoid lossy wchar->char conversion; JSON must be UTF-8.
        int bytes = WideCharToMultiByte(CP_UTF8, 0, mapNameW.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string mapName;
        if (bytes > 1)
        {
            mapName.resize(static_cast<std::size_t>(bytes - 1));
            WideCharToMultiByte(CP_UTF8, 0, mapNameW.c_str(), -1, mapName.data(), bytes, nullptr, nullptr);
        }
        return "{\"type\":\"hookState\",\"payload\":{\"pid\":" + std::to_string(pid) + ",\"state\":\"" + JsonEscape(state) +
               "\",\"reason\":\"" + JsonEscape(reason) + "\",\"api\":\"DX11\",\"frameMap\":\"" + JsonEscape(mapName) + "\"}}\n";
    }

    std::wstring GetExeDir()
    {
        wchar_t path[MAX_PATH]{};
        const DWORD got = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
        if (got == 0 || got >= std::size(path))
        {
            return L".";
        }

        std::wstring s(path);
        const auto pos = s.find_last_of(L"\\/");
        if (pos == std::wstring::npos)
        {
            return L".";
        }
        return s.substr(0, pos);
    }

    std::wstring JoinPath(const std::wstring& dir, const std::wstring& leaf)
    {
        if (dir.empty())
        {
            return leaf;
        }
        if (dir.back() == L'\\' || dir.back() == L'/')
        {
            return dir + leaf;
        }
        return dir + L"\\" + leaf;
    }

    bool ExtractU32(const std::string& json, const char* key, std::uint32_t& out)
    {
        const std::string needle = std::string("\"") + key + "\"";
        const auto kpos = json.find(needle);
        if (kpos == std::string::npos)
        {
            return false;
        }
        const auto cpos = json.find(':', kpos + needle.size());
        if (cpos == std::string::npos)
        {
            return false;
        }
        std::size_t i = cpos + 1;
        while (i < json.size() && (json[i] == ' ' || json[i] == '\t'))
        {
            ++i;
        }

        std::uint32_t val = 0;
        bool any = false;
        while (i < json.size() && json[i] >= '0' && json[i] <= '9')
        {
            any = true;
            val = (val * 10u) + static_cast<std::uint32_t>(json[i] - '0');
            ++i;
        }

        if (!any)
        {
            return false;
        }

        out = val;
        return true;
    }

    bool ExtractBool(const std::string& json, const char* key, bool& out)
    {
        const std::string needle = std::string("\"") + key + "\"";
        const auto kpos = json.find(needle);
        if (kpos == std::string::npos)
        {
            return false;
        }
        const auto cpos = json.find(':', kpos + needle.size());
        if (cpos == std::string::npos)
        {
            return false;
        }

        const auto tpos = json.find("true", cpos);
        const auto fpos = json.find("false", cpos);
        if (tpos != std::string::npos && tpos < cpos + 16)
        {
            out = true;
            return true;
        }
        if (fpos != std::string::npos && fpos < cpos + 16)
        {
            out = false;
            return true;
        }
        return false;
    }

    bool ExtractString(const std::string& json, const char* key, std::string& out)
    {
        const std::string needle = std::string("\"") + key + "\"";
        const auto kpos = json.find(needle);
        if (kpos == std::string::npos)
        {
            return false;
        }
        const auto cpos = json.find(':', kpos + needle.size());
        if (cpos == std::string::npos)
        {
            return false;
        }

        auto q1 = json.find('"', cpos + 1);
        if (q1 == std::string::npos)
        {
            return false;
        }
        auto q2 = json.find('"', q1 + 1);
        if (q2 == std::string::npos || q2 <= q1 + 1)
        {
            return false;
        }

        out = json.substr(q1 + 1, q2 - (q1 + 1));
        return true;
    }

    bool Base64Decode(const std::string& b64, std::vector<std::uint8_t>& outBytes)
    {
        outBytes.clear();
        if (b64.empty())
        {
            return true;
        }

        DWORD required = 0;
        if (!CryptStringToBinaryA(b64.c_str(), static_cast<DWORD>(b64.size()), CRYPT_STRING_BASE64, nullptr, &required, nullptr, nullptr))
        {
            return false;
        }

        outBytes.resize(required);
        if (!CryptStringToBinaryA(
                b64.c_str(),
                static_cast<DWORD>(b64.size()),
                CRYPT_STRING_BASE64,
                outBytes.data(),
                &required,
                nullptr,
                nullptr))
        {
            outBytes.clear();
            return false;
        }

        if (required != outBytes.size())
        {
            outBytes.resize(required);
        }

        return true;
    }

    bool JsonUnescapeString(const std::string& input, std::string& output)
    {
        output.clear();
        output.reserve(input.size());

        for (std::size_t i = 0; i < input.size(); i++)
        {
            const char ch = input[i];
            if (ch != '\\')
            {
                output.push_back(ch);
                continue;
            }

            if (i + 1 >= input.size())
            {
                return false;
            }

            const char esc = input[i + 1];
            switch (esc)
            {
            case '"':
                output.push_back('"');
                i += 1;
                break;
            case '\\':
                output.push_back('\\');
                i += 1;
                break;
            case '/':
                output.push_back('/');
                i += 1;
                break;
            case 'b':
                output.push_back('\b');
                i += 1;
                break;
            case 'f':
                output.push_back('\f');
                i += 1;
                break;
            case 'n':
                output.push_back('\n');
                i += 1;
                break;
            case 'r':
                output.push_back('\r');
                i += 1;
                break;
            case 't':
                output.push_back('\t');
                i += 1;
                break;
            case 'u':
            {
                if (i + 5 >= input.size())
                {
                    return false;
                }

                auto hex = [](char c) -> int
                {
                    if (c >= '0' && c <= '9') return c - '0';
                    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
                    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
                    return -1;
                };

                const int h0 = hex(input[i + 2]);
                const int h1 = hex(input[i + 3]);
                const int h2 = hex(input[i + 4]);
                const int h3 = hex(input[i + 5]);
                if (h0 < 0 || h1 < 0 || h2 < 0 || h3 < 0)
                {
                    return false;
                }

                const int code = (h0 << 12) | (h1 << 8) | (h2 << 4) | h3;
                if (code < 0 || code > 0x7F)
                {
                    // NOTE: overlayUpdate v1 payload should be ASCII only (base64).
                    return false;
                }

                output.push_back(static_cast<char>(code));
                i += 5;
                break;
            }
            default:
                return false;
            }
        }

        return true;
    }

    bool ParseAttach(const std::string& json, AttachRequest& outReq)
    {
        std::uint32_t pid = 0;
        if (!ExtractU32(json, "pid", pid) || pid == 0)
        {
            return false;
        }

        outReq.pid = static_cast<DWORD>(pid);
        (void)ExtractBool(json, "enableOverlay", outReq.enableOverlay);

        std::uint32_t fps = 15;
        if (ExtractU32(json, "captureFpsLimit", fps))
        {
            outReq.captureFpsLimit = fps;
        }

        return true;
    }

    bool ParseDetach(const std::string& json, DWORD& pid)
    {
        std::uint32_t outPid = 0;
        if (!ExtractU32(json, "pid", outPid) || outPid == 0)
        {
            return false;
        }
        pid = static_cast<DWORD>(outPid);
        return true;
    }

    bool RemoteCallNoArg(HANDLE process, void* remoteFn, DWORD& exitCode)
    {
        exitCode = 0;
        HANDLE thread = CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteFn), nullptr, 0, nullptr);
        if (thread == nullptr)
        {
            return false;
        }

        const DWORD wait = WaitForSingleObject(thread, kRemoteTimeoutMs);
        if (wait != WAIT_OBJECT_0)
        {
            CloseHandle(thread);
            return false;
        }

        DWORD code = 0;
        GetExitCodeThread(thread, &code);
        CloseHandle(thread);
        exitCode = code;
        return true;
    }

    bool RemoteCallOneArg(HANDLE process, void* remoteFn, void* arg, DWORD& exitCode)
    {
        exitCode = 0;
        HANDLE thread = CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteFn), arg, 0, nullptr);
        if (thread == nullptr)
        {
            return false;
        }

        const DWORD wait = WaitForSingleObject(thread, kRemoteTimeoutMs);
        if (wait != WAIT_OBJECT_0)
        {
            CloseHandle(thread);
            return false;
        }

        DWORD code = 0;
        GetExitCodeThread(thread, &code);
        CloseHandle(thread);
        exitCode = code;
        return true;
    }

    HMODULE FindRemoteModuleBase(DWORD pid, const std::wstring& leafName)
    {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snap == INVALID_HANDLE_VALUE)
        {
            return nullptr;
        }

        MODULEENTRY32W me{};
        me.dwSize = sizeof(me);
        if (!Module32FirstW(snap, &me))
        {
            CloseHandle(snap);
            return nullptr;
        }

        do
        {
            if (_wcsicmp(me.szModule, leafName.c_str()) == 0)
            {
                CloseHandle(snap);
                return reinterpret_cast<HMODULE>(me.modBaseAddr);
            }
        } while (Module32NextW(snap, &me));

        CloseHandle(snap);
        return nullptr;
    }

    bool RemoteCallExportNoArg(HANDLE process, const std::wstring& dllPath, HMODULE remoteModule, const char* exportName, DWORD& exitCode)
    {
        exitCode = 0;
        HMODULE localModule = LoadLibraryW(dllPath.c_str());
        if (localModule == nullptr)
        {
            return false;
        }

        const auto localFn = reinterpret_cast<std::uintptr_t>(GetProcAddress(localModule, exportName));
        if (localFn == 0)
        {
            FreeLibrary(localModule);
            return false;
        }

        const std::uintptr_t localBase = reinterpret_cast<std::uintptr_t>(localModule);
        const std::uintptr_t remoteBase = reinterpret_cast<std::uintptr_t>(remoteModule);
        const std::uintptr_t offset = localFn - localBase;
        const auto remoteFn = reinterpret_cast<void*>(remoteBase + offset);
        FreeLibrary(localModule);

        return RemoteCallNoArg(process, remoteFn, exitCode);
    }

    bool InjectDx11Agent(DWORD pid, const std::wstring& dllPath, HMODULE& outRemoteModule, std::string& outReason)
    {
        outRemoteModule = nullptr;

        HANDLE process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            FALSE,
            pid);
        if (process == nullptr)
        {
            outReason = "OpenProcess_failed";
            return false;
        }

        const std::size_t bytes = (dllPath.size() + 1) * sizeof(wchar_t);
        void* remoteStr = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteStr == nullptr)
        {
            CloseHandle(process);
            outReason = "VirtualAllocEx_failed";
            return false;
        }

        SIZE_T written = 0;
        if (!WriteProcessMemory(process, remoteStr, dllPath.c_str(), bytes, &written) || written != bytes)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "WriteProcessMemory_failed";
            return false;
        }

        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        const auto loadLib = reinterpret_cast<void*>(GetProcAddress(k32, "LoadLibraryW"));
        if (loadLib == nullptr)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "GetProcAddress_LoadLibraryW_failed";
            return false;
        }

        DWORD loadExit = 0;
        if (!RemoteCallOneArg(process, loadLib, remoteStr, loadExit) || loadExit == 0)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "Remote_LoadLibraryW_failed";
            return false;
        }

        VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);

        outRemoteModule = FindRemoteModuleBase(pid, L"HookAgentDx11.dll");
        if (outRemoteModule == nullptr)
        {
            CloseHandle(process);
            outReason = "Remote_module_not_found";
            return false;
        }

        DWORD installExit = 0;
        const bool okInstall = RemoteCallExportNoArg(process, dllPath, outRemoteModule, "InstallDx11HookThread", installExit);
        CloseHandle(process);

        if (!okInstall || installExit == 0)
        {
            outReason = "Remote_InstallDx11Hook_failed";
            return false;
        }

        outReason = "ok";
        return true;
    }

    bool UninstallDx11Agent(const ProcessHookState& st, std::string& outReason)
    {
        HANDLE process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            FALSE,
            st.pid);
        if (process == nullptr)
        {
            outReason = "OpenProcess_failed";
            return false;
        }

        DWORD uninstallExit = 0;
        (void)RemoteCallExportNoArg(process, st.dllPath, st.remoteModule, "UninstallDx11HookThread", uninstallExit);
        CloseHandle(process);
        outReason = "ok";
        return true;
    }

    void HandleMessage(HANDLE pipe, const std::string& message)
    {
        if (message.find("\"type\":\"attach\"") != std::string::npos)
        {
            AttachRequest req{};
            if (!ParseAttach(message, req))
            {
                WriteResponse(pipe, BuildState("Failed", "attach_parse_failed", ht::hook::ipc::GraphicsApi::Dx11, 0));
                return;
            }

            const auto existing = g_states.find(req.pid);
            if (existing != g_states.end() && existing->second.remoteModule != nullptr)
            {
                (void)existing->second.configWriter.Write(req.pid, existing->second.api, req.captureFpsLimit, req.enableOverlay);
                (void)existing->second.overlayWriter.Write(req.pid, existing->second.api, nullptr, 0);
                // WHY: vtable patching cannot safely unload in v1. Re-attach re-enables by calling Install again.
                HANDLE process = OpenProcess(
                    PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                    FALSE,
                    req.pid);
                if (process != nullptr)
                {
                    DWORD installExit = 0;
                    const bool ok = RemoteCallExportNoArg(
                        process,
                        existing->second.dllPath,
                        existing->second.remoteModule,
                        "InstallDx11HookThread",
                        installExit);
                    CloseHandle(process);
                    if (ok && installExit != 0)
                    {
                        WriteResponse(pipe, BuildState("Attached", "ok_existing", existing->second.api, req.pid));
                        return;
                    }
                }
            }

            const auto exeDir = GetExeDir();
            const auto dllPath = JoinPath(exeDir, L"HookAgentDx11.dll");

            std::string reason;
            HMODULE remoteModule = nullptr;
            const bool ok = InjectDx11Agent(req.pid, dllPath, remoteModule, reason);
            if (!ok)
            {
                WriteResponse(pipe, BuildState("Failed", "attach_failed:" + reason, ht::hook::ipc::GraphicsApi::Dx11, req.pid));
                return;
            }

            ProcessHookState st{};
            st.pid = req.pid;
            st.remoteModule = remoteModule;
            st.dllPath = dllPath;
            st.api = ht::hook::ipc::GraphicsApi::Dx11;
            (void)st.configWriter.Write(req.pid, st.api, req.captureFpsLimit, req.enableOverlay);
            (void)st.overlayWriter.Write(req.pid, st.api, nullptr, 0);
            g_states[req.pid] = std::move(st);

            WriteResponse(pipe, BuildState("Attached", "ok", ht::hook::ipc::GraphicsApi::Dx11, req.pid));
            return;
        }

        if (message.find("\"type\":\"detach\"") != std::string::npos)
        {
            DWORD pid = 0;
            if (!ParseDetach(message, pid))
            {
                WriteResponse(pipe, BuildState("Failed", "detach_parse_failed", ht::hook::ipc::GraphicsApi::Dx11, 0));
                return;
            }

            const auto it = g_states.find(pid);
            if (it == g_states.end())
            {
                WriteResponse(pipe, BuildState("Detached", "not_attached", ht::hook::ipc::GraphicsApi::Dx11, pid));
                return;
            }

            std::string reason;
            (void)UninstallDx11Agent(it->second, reason);

            // WHY: With vtable patching, unloading the agent DLL would leave dangling function pointers in swapchain vtables.
            // We keep the module loaded for process lifetime in v1; detach only disables capture.
            it->second.configWriter.Reset();
            it->second.overlayWriter.Reset();
            WriteResponse(pipe, BuildState("Detached", "ok_disabled", ht::hook::ipc::GraphicsApi::Dx11, pid));
            return;
        }

        if (message.find("\"type\":\"overlayUpdate\"") != std::string::npos)
        {
            DWORD pid = 0;
            if (!ParseDetach(message, pid))
            {
                WriteResponse(pipe, BuildState("Failed", "overlay_parse_failed", ht::hook::ipc::GraphicsApi::Dx11, 0));
                return;
            }

            std::uint32_t count = 0;
            (void)ExtractU32(message, "count", count);
            std::string rectsB64;
            if (!ExtractString(message, "rectsB64", rectsB64))
            {
                rectsB64.clear();
            }

            const auto it = g_states.find(pid);
            if (it == g_states.end())
            {
                WriteResponse(pipe, BuildState("Failed", "overlay_pid_not_attached", ht::hook::ipc::GraphicsApi::Dx11, pid));
                return;
            }

            std::string rectsB64Unescaped;
            if (!rectsB64.empty())
            {
                if (!JsonUnescapeString(rectsB64, rectsB64Unescaped))
                {
                    WriteResponse(pipe, BuildState("Failed", "overlay_unescape_failed", it->second.api, pid));
                    return;
                }
            }

            std::vector<std::uint8_t> decoded;
            if (!Base64Decode(rectsB64Unescaped.empty() ? rectsB64 : rectsB64Unescaped, decoded))
            {
                WriteResponse(pipe, BuildState("Failed", "overlay_decode_failed", it->second.api, pid));
                return;
            }

            const std::size_t cmdSize = sizeof(ht::hook::ipc::OverlayRectCommand);
            const std::size_t expectedBytes = static_cast<std::size_t>(count) * cmdSize;
            if (!decoded.empty() && decoded.size() < expectedBytes)
            {
                WriteResponse(pipe, BuildState("Failed", "overlay_payload_too_small", it->second.api, pid));
                return;
            }

            const auto* cmds = decoded.empty()
                ? nullptr
                : reinterpret_cast<const ht::hook::ipc::OverlayRectCommand*>(decoded.data());

            const bool ok = it->second.overlayWriter.Write(pid, it->second.api, cmds, static_cast<std::size_t>(count));
            WriteResponse(pipe, BuildState("Running", ok ? "overlay_ok" : "overlay_write_failed", it->second.api, pid));
            return;
        }

        WriteResponse(pipe, BuildState("Running", "unknown_message", ht::hook::ipc::GraphicsApi::Dx11, 0));
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
        std::vector<char> buffer(kBufferBytes);

        for (;;)
        {
            DWORD bytesRead = 0;
            const BOOL ok = ReadFile(pipe, buffer.data(), static_cast<DWORD>(buffer.size()), &bytesRead, nullptr);
            if (!ok || bytesRead == 0)
            {
                break;
            }

            pending.append(buffer.data(), buffer.data() + bytesRead);
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
