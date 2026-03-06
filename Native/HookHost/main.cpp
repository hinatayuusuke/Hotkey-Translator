#include <windows.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <iterator>
#include <mutex>
#include <string>
#include <tlhelp32.h>
#include <unordered_map>
#include <vector>

#include "../HookCommon/HookIpcProtocol.h"
#include "../HookCommon/SharedHookConfig.h"

namespace
{
    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\hotkey_translator_hook)";
    constexpr DWORD kBufferBytes = 64 * 1024;
    constexpr DWORD kRemoteTimeoutMs = 5000;

    struct AttachRequest
    {
        DWORD pid = 0;
        ht::hook::ipc::GraphicsApi api = ht::hook::ipc::GraphicsApi::Dx11;
        bool enableOverlay = true;
        std::uint32_t captureFpsLimit = 15;
        std::uint32_t configFlags = 0;
    };

    struct ProcessHookState
    {
        DWORD pid = 0;
        HMODULE remoteModule = nullptr;
        std::wstring dllPath;
        ht::hook::ipc::GraphicsApi api = ht::hook::ipc::GraphicsApi::Dx11;
        ht::hook::ipc::SharedHookConfigWriter configWriter;
    };

    std::unordered_map<DWORD, ProcessHookState> g_states;
    bool g_shutdownRequested = false;
    std::mutex g_diagFileMutex;
    HANDLE g_diagFileHandle = INVALID_HANDLE_VALUE;
    DWORD g_diagFilePid = 0;
    std::wstring g_diagFilePath;

    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int bytes = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (bytes <= 1)
        {
            return {};
        }

        std::string out(static_cast<std::size_t>(bytes - 1), '\0');
        (void)WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, out.data(), bytes, nullptr, nullptr);
        return out;
    }

    bool EnsureDiagFileUnlocked()
    {
        const DWORD pid = GetCurrentProcessId();
        if (g_diagFileHandle != INVALID_HANDLE_VALUE && g_diagFilePid == pid)
        {
            return true;
        }

        if (g_diagFileHandle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(g_diagFileHandle);
            g_diagFileHandle = INVALID_HANDLE_VALUE;
            g_diagFilePid = 0;
            g_diagFilePath.clear();
        }

        wchar_t tempPath[MAX_PATH]{};
        const DWORD tempLen = GetTempPathW(static_cast<DWORD>(std::size(tempPath)), tempPath);
        if (tempLen == 0 || tempLen >= std::size(tempPath))
        {
            return false;
        }

        std::wstring dir = tempPath;
        if (!dir.empty() && dir.back() != L'\\' && dir.back() != L'/')
        {
            dir += L'\\';
        }
        dir += L"HotkeyTranslator";
        (void)CreateDirectoryW(dir.c_str(), nullptr);

        wchar_t fileName[128]{};
        (void)swprintf_s(fileName, L"hook_host_%lu.log", static_cast<unsigned long>(pid));
        std::wstring filePath = dir;
        filePath += L'\\';
        filePath += fileName;

        HANDLE file = CreateFileW(
            filePath.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        g_diagFileHandle = file;
        g_diagFilePid = pid;
        g_diagFilePath = std::move(filePath);

        const auto pathUtf8 = WideToUtf8(g_diagFilePath);
        if (!pathUtf8.empty())
        {
            char openMsg[512]{};
            (void)_snprintf_s(
                openMsg,
                sizeof(openMsg),
                _TRUNCATE,
                "stage=hook_host event=file_log_open pid=%lu path=\"%s\".",
                static_cast<unsigned long>(pid),
                pathUtf8.c_str());
            OutputDebugStringA(openMsg);
            OutputDebugStringA("\n");
        }

        return true;
    }

    void AppendDiagFileLine(const char* line)
    {
        if (line == nullptr || line[0] == '\0')
        {
            return;
        }

        std::lock_guard<std::mutex> lock(g_diagFileMutex);
        if (!EnsureDiagFileUnlocked())
        {
            return;
        }

        SYSTEMTIME st{};
        GetLocalTime(&st);
        char prefix[64]{};
        (void)_snprintf_s(
            prefix,
            sizeof(prefix),
            _TRUNCATE,
            "%02u:%02u:%02u.%03u ",
            static_cast<unsigned int>(st.wHour),
            static_cast<unsigned int>(st.wMinute),
            static_cast<unsigned int>(st.wSecond),
            static_cast<unsigned int>(st.wMilliseconds));

        DWORD written = 0;
        (void)WriteFile(g_diagFileHandle, prefix, static_cast<DWORD>(std::strlen(prefix)), &written, nullptr);
        (void)WriteFile(g_diagFileHandle, line, static_cast<DWORD>(std::strlen(line)), &written, nullptr);
        static constexpr char kNewLine[] = "\r\n";
        (void)WriteFile(g_diagFileHandle, kNewLine, static_cast<DWORD>(sizeof(kNewLine) - 1), &written, nullptr);
    }

    void CloseDiagFile()
    {
        std::lock_guard<std::mutex> lock(g_diagFileMutex);
        if (g_diagFileHandle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(g_diagFileHandle);
            g_diagFileHandle = INVALID_HANDLE_VALUE;
        }

        g_diagFilePid = 0;
        g_diagFilePath.clear();
    }

    const char* ApiToLogString(ht::hook::ipc::GraphicsApi api)
    {
        switch (api)
        {
            case ht::hook::ipc::GraphicsApi::Dx11: return "Dx11";
            case ht::hook::ipc::GraphicsApi::Dx12: return "Dx12";
            case ht::hook::ipc::GraphicsApi::OpenGl: return "OpenGL";
            case ht::hook::ipc::GraphicsApi::Vulkan: return "Vulkan";
            case ht::hook::ipc::GraphicsApi::Dx9: return "Dx9";
            default: return "Unknown";
        }
    }

    void LogHost(const char* format, ...)
    {
        if (format == nullptr)
        {
            return;
        }

        char payload[1024]{};
        va_list args;
        va_start(args, format);
        (void)_vsnprintf_s(payload, sizeof(payload), _TRUNCATE, format, args);
        va_end(args);

        char line[1200]{};
        (void)_snprintf_s(line, sizeof(line), _TRUNCATE, "stage=hook_host %s", payload);
        OutputDebugStringA(line);
        OutputDebugStringA("\n");
        AppendDiagFileLine(line);
    }

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
        const char* apiName = "Unknown";
        switch (api)
        {
            case ht::hook::ipc::GraphicsApi::Dx11: apiName = "DX11"; break;
            case ht::hook::ipc::GraphicsApi::Dx12: apiName = "DX12"; break;
            case ht::hook::ipc::GraphicsApi::OpenGl: apiName = "OpenGL"; break;
            case ht::hook::ipc::GraphicsApi::Vulkan: apiName = "Vulkan"; break;
            case ht::hook::ipc::GraphicsApi::Dx9: apiName = "DX9"; break;
            default: break;
        }

        return "{\"type\":\"hookState\",\"payload\":{\"pid\":" + std::to_string(pid) + ",\"state\":\"" + JsonEscape(state) +
               "\",\"reason\":\"" + JsonEscape(reason) + "\",\"api\":\"" + JsonEscape(apiName) + "\",\"frameMap\":\"" + JsonEscape(mapName) + "\"}}\n";
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

    const wchar_t* ResolveAgentDllLeaf(ht::hook::ipc::GraphicsApi api)
    {
        switch (api)
        {
            case ht::hook::ipc::GraphicsApi::Dx11:
                return L"HookAgentDx11.dll";
            case ht::hook::ipc::GraphicsApi::Vulkan:
                return L"HookAgentVulkan.dll";
            case ht::hook::ipc::GraphicsApi::Dx9:
                return L"HookAgentDx9.dll";
            default:
                return nullptr;
        }
    }

    const char* ResolveInstallExport(ht::hook::ipc::GraphicsApi api)
    {
        switch (api)
        {
            case ht::hook::ipc::GraphicsApi::Dx11:
                return "InstallDx11HookThread";
            case ht::hook::ipc::GraphicsApi::Vulkan:
                return "InstallVulkanHookThread";
            case ht::hook::ipc::GraphicsApi::Dx9:
                return "InstallDx9HookThread";
            default:
                return nullptr;
        }
    }

    const char* ResolveUninstallExport(ht::hook::ipc::GraphicsApi api)
    {
        switch (api)
        {
            case ht::hook::ipc::GraphicsApi::Dx11:
                return "UninstallDx11HookThread";
            case ht::hook::ipc::GraphicsApi::Vulkan:
                return "UninstallVulkanHookThread";
            case ht::hook::ipc::GraphicsApi::Dx9:
                return "UninstallDx9HookThread";
            default:
                return nullptr;
        }
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

    bool ParseAttach(const std::string& json, AttachRequest& outReq)
    {
        std::uint32_t pid = 0;
        if (!ExtractU32(json, "pid", pid) || pid == 0)
        {
            return false;
        }

        outReq.pid = static_cast<DWORD>(pid);

        std::uint32_t apiRaw = 0;
        if (!ExtractU32(json, "api", apiRaw))
        {
            return false;
        }

        switch (static_cast<ht::hook::ipc::GraphicsApi>(apiRaw))
        {
            case ht::hook::ipc::GraphicsApi::Dx11:
            case ht::hook::ipc::GraphicsApi::Dx12:
            case ht::hook::ipc::GraphicsApi::OpenGl:
            case ht::hook::ipc::GraphicsApi::Vulkan:
            case ht::hook::ipc::GraphicsApi::Dx9:
                outReq.api = static_cast<ht::hook::ipc::GraphicsApi>(apiRaw);
                break;
            default:
                return false;
        }

        (void)ExtractBool(json, "enableOverlay", outReq.enableOverlay);

        std::uint32_t fps = 15;
        if (ExtractU32(json, "captureFpsLimit", fps))
        {
            outReq.captureFpsLimit = fps;
        }

        std::uint32_t flags = 0;
        if (ExtractU32(json, "configFlags", flags))
        {
            outReq.configFlags = flags;
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
            LogHost("event=remote_call_noarg_failed reason=create_remote_thread_failed gle=%lu fn=%p.", static_cast<unsigned long>(GetLastError()), remoteFn);
            return false;
        }

        const DWORD wait = WaitForSingleObject(thread, kRemoteTimeoutMs);
        if (wait != WAIT_OBJECT_0)
        {
            LogHost("event=remote_call_noarg_failed reason=wait_failed wait=%lu fn=%p.", static_cast<unsigned long>(wait), remoteFn);
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
            LogHost("event=remote_call_onearg_failed reason=create_remote_thread_failed gle=%lu fn=%p arg=%p.", static_cast<unsigned long>(GetLastError()), remoteFn, arg);
            return false;
        }

        const DWORD wait = WaitForSingleObject(thread, kRemoteTimeoutMs);
        if (wait != WAIT_OBJECT_0)
        {
            LogHost("event=remote_call_onearg_failed reason=wait_failed wait=%lu fn=%p arg=%p.", static_cast<unsigned long>(wait), remoteFn, arg);
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
            LogHost(
                "event=find_remote_module_failed reason=snapshot_failed pid=%lu leaf=\"%ls\" gle=%lu.",
                static_cast<unsigned long>(pid),
                leafName.c_str(),
                static_cast<unsigned long>(GetLastError()));
            return nullptr;
        }

        MODULEENTRY32W me{};
        me.dwSize = sizeof(me);
        if (!Module32FirstW(snap, &me))
        {
            LogHost(
                "event=find_remote_module_failed reason=module32first_failed pid=%lu leaf=\"%ls\" gle=%lu.",
                static_cast<unsigned long>(pid),
                leafName.c_str(),
                static_cast<unsigned long>(GetLastError()));
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
        LogHost("event=find_remote_module_failed reason=leaf_not_found pid=%lu leaf=\"%ls\".", static_cast<unsigned long>(pid), leafName.c_str());
        return nullptr;
    }

    bool RemoteCallExportNoArg(HANDLE process, const std::wstring& dllPath, HMODULE remoteModule, const char* exportName, DWORD& exitCode)
    {
        exitCode = 0;
        HMODULE localModule = LoadLibraryW(dllPath.c_str());
        if (localModule == nullptr)
        {
            LogHost(
                "event=resolve_export_failed reason=local_loadlibrary_failed dll=\"%ls\" export=%s gle=%lu.",
                dllPath.c_str(),
                exportName != nullptr ? exportName : "null",
                static_cast<unsigned long>(GetLastError()));
            return false;
        }

        const auto localFn = reinterpret_cast<std::uintptr_t>(GetProcAddress(localModule, exportName));
        if (localFn == 0)
        {
            LogHost(
                "event=resolve_export_failed reason=getprocaddress_failed dll=\"%ls\" export=%s gle=%lu.",
                dllPath.c_str(),
                exportName != nullptr ? exportName : "null",
                static_cast<unsigned long>(GetLastError()));
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

    bool InjectAgent(
        DWORD pid,
        ht::hook::ipc::GraphicsApi api,
        const std::wstring& dllPath,
        HMODULE& outRemoteModule,
        std::string& outReason)
    {
        outRemoteModule = nullptr;

        HANDLE process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            FALSE,
            pid);
        if (process == nullptr)
        {
            outReason = "OpenProcess_failed";
            LogHost(
                "event=inject_failed reason=%s pid=%lu api=%s gle=%lu.",
                outReason.c_str(),
                static_cast<unsigned long>(pid),
                ApiToLogString(api),
                static_cast<unsigned long>(GetLastError()));
            return false;
        }

        LogHost("event=inject_begin pid=%lu api=%s dll=\"%ls\".", static_cast<unsigned long>(pid), ApiToLogString(api), dllPath.c_str());

        const std::size_t bytes = (dllPath.size() + 1) * sizeof(wchar_t);
        void* remoteStr = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteStr == nullptr)
        {
            CloseHandle(process);
            outReason = "VirtualAllocEx_failed";
            LogHost("event=inject_failed reason=%s pid=%lu gle=%lu.", outReason.c_str(), static_cast<unsigned long>(pid), static_cast<unsigned long>(GetLastError()));
            return false;
        }

        SIZE_T written = 0;
        if (!WriteProcessMemory(process, remoteStr, dllPath.c_str(), bytes, &written) || written != bytes)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "WriteProcessMemory_failed";
            LogHost("event=inject_failed reason=%s pid=%lu gle=%lu written=%llu expected=%llu.", outReason.c_str(), static_cast<unsigned long>(pid), static_cast<unsigned long>(GetLastError()), static_cast<unsigned long long>(written), static_cast<unsigned long long>(bytes));
            return false;
        }

        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        const auto loadLib = reinterpret_cast<void*>(GetProcAddress(k32, "LoadLibraryW"));
        if (loadLib == nullptr)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "GetProcAddress_LoadLibraryW_failed";
            LogHost("event=inject_failed reason=%s pid=%lu gle=%lu.", outReason.c_str(), static_cast<unsigned long>(pid), static_cast<unsigned long>(GetLastError()));
            return false;
        }

        DWORD loadExit = 0;
        if (!RemoteCallOneArg(process, loadLib, remoteStr, loadExit) || loadExit == 0)
        {
            VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);
            CloseHandle(process);
            outReason = "Remote_LoadLibraryW_failed";
            LogHost("event=inject_failed reason=%s pid=%lu loadExit=%lu.", outReason.c_str(), static_cast<unsigned long>(pid), static_cast<unsigned long>(loadExit));
            return false;
        }

        LogHost("event=inject_loadlibrary_ok pid=%lu loadExit=%lu.", static_cast<unsigned long>(pid), static_cast<unsigned long>(loadExit));

        VirtualFreeEx(process, remoteStr, 0, MEM_RELEASE);

        const auto dllLeaf = ResolveAgentDllLeaf(api);
        const auto installExport = ResolveInstallExport(api);
        if (dllLeaf == nullptr || installExport == nullptr)
        {
            CloseHandle(process);
            outReason = "api_not_implemented";
            LogHost("event=inject_failed reason=%s pid=%lu.", outReason.c_str(), static_cast<unsigned long>(pid));
            return false;
        }

        outRemoteModule = FindRemoteModuleBase(pid, dllLeaf);
        if (outRemoteModule == nullptr)
        {
            CloseHandle(process);
            outReason = "Remote_module_not_found";
            LogHost("event=inject_failed reason=%s pid=%lu leaf=\"%ls\".", outReason.c_str(), static_cast<unsigned long>(pid), dllLeaf);
            return false;
        }

        LogHost("event=inject_module_found pid=%lu module=%p leaf=\"%ls\".", static_cast<unsigned long>(pid), outRemoteModule, dllLeaf);

        DWORD installExit = 0;
        const bool okInstall = RemoteCallExportNoArg(process, dllPath, outRemoteModule, installExport, installExit);
        CloseHandle(process);

        if (!okInstall || installExit == 0)
        {
            outReason = "Remote_install_hook_failed";
            LogHost("event=inject_failed reason=%s pid=%lu export=%s ok=%u installExit=%lu.", outReason.c_str(), static_cast<unsigned long>(pid), installExport, okInstall ? 1u : 0u, static_cast<unsigned long>(installExit));
            return false;
        }

        outReason = "ok";
        LogHost("event=inject_ok pid=%lu export=%s installExit=%lu.", static_cast<unsigned long>(pid), installExport, static_cast<unsigned long>(installExit));
        return true;
    }

    bool UninstallAgent(const ProcessHookState& st, std::string& outReason)
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

        const auto uninstallExport = ResolveUninstallExport(st.api);
        if (uninstallExport == nullptr)
        {
            CloseHandle(process);
            outReason = "api_not_implemented";
            return false;
        }

        DWORD uninstallExit = 0;
        (void)RemoteCallExportNoArg(process, st.dllPath, st.remoteModule, uninstallExport, uninstallExit);
        CloseHandle(process);
        outReason = "ok";
        return true;
    }

    void DisableAllHooksForShutdown()
    {
        for (auto& entry : g_states)
        {
            std::string reason;
            (void)UninstallAgent(entry.second, reason);
            entry.second.configWriter.Reset();
        }

        g_states.clear();
    }

    DWORD GetParentProcessId(DWORD pid)
    {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE)
        {
            return 0;
        }

        PROCESSENTRY32W pe{};
        pe.dwSize = sizeof(pe);
        if (!Process32FirstW(snap, &pe))
        {
            CloseHandle(snap);
            return 0;
        }

        do
        {
            if (pe.th32ProcessID == pid)
            {
                const DWORD parentPid = pe.th32ParentProcessID;
                CloseHandle(snap);
                return parentPid;
            }
        } while (Process32NextW(snap, &pe));

        CloseHandle(snap);
        return 0;
    }

    bool IsProcessAlive(DWORD pid)
    {
        if (pid == 0)
        {
            return false;
        }

        HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
        if (process == nullptr)
        {
            return false;
        }

        const DWORD wait = WaitForSingleObject(process, 0);
        CloseHandle(process);
        return wait == WAIT_TIMEOUT;
    }

    void HandleMessage(HANDLE pipe, const std::string& message)
    {
        if (message.find("\"type\":\"attach\"") != std::string::npos)
        {
            AttachRequest req{};
            if (!ParseAttach(message, req))
            {
                LogHost("event=attach_parse_failed.");
                WriteResponse(pipe, BuildState("Failed", "attach_parse_failed", ht::hook::ipc::GraphicsApi::Dx11, 0));
                return;
            }

            LogHost(
                "event=attach_received pid=%lu api=%s fps=%u overlay=%u flags=0x%08X.",
                static_cast<unsigned long>(req.pid),
                ApiToLogString(req.api),
                req.captureFpsLimit,
                req.enableOverlay ? 1u : 0u,
                static_cast<unsigned int>(req.configFlags));

            const auto existing = g_states.find(req.pid);
            if (existing != g_states.end() && existing->second.remoteModule != nullptr)
            {
                if (existing->second.api != req.api)
                {
                    LogHost(
                        "event=attach_reject reason=api_mismatch_existing pid=%lu existingApi=%s requestedApi=%s.",
                        static_cast<unsigned long>(req.pid),
                        ApiToLogString(existing->second.api),
                        ApiToLogString(req.api));
                    WriteResponse(pipe, BuildState("Failed", "attach_failed:api_mismatch_existing", req.api, req.pid));
                    return;
                }

                (void)existing->second.configWriter.Write(
                    req.pid,
                    existing->second.api,
                    req.captureFpsLimit,
                    req.enableOverlay,
                    req.configFlags);
                // WHY: vtable patching cannot safely unload in v1. Re-attach re-enables by calling Install again.
                HANDLE process = OpenProcess(
                    PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                    FALSE,
                    req.pid);
                if (process != nullptr)
                {
                    DWORD installExit = 0;
                    const auto installExport = ResolveInstallExport(existing->second.api);
                    if (installExport == nullptr)
                    {
                        CloseHandle(process);
                        LogHost("event=attach_reapply_failed reason=api_not_implemented pid=%lu.", static_cast<unsigned long>(req.pid));
                        WriteResponse(pipe, BuildState("Failed", "attach_failed:api_not_implemented", existing->second.api, req.pid));
                        return;
                    }
                    const bool ok = RemoteCallExportNoArg(
                        process,
                        existing->second.dllPath,
                        existing->second.remoteModule,
                        installExport,
                        installExit);
                    CloseHandle(process);
                    if (ok && installExit != 0)
                    {
                        LogHost(
                            "event=attach_reapply_ok pid=%lu export=%s exit=%lu.",
                            static_cast<unsigned long>(req.pid),
                            installExport,
                            static_cast<unsigned long>(installExit));
                        WriteResponse(pipe, BuildState("Attached", "ok_existing", existing->second.api, req.pid));
                        return;
                    }

                    LogHost(
                        "event=attach_reapply_failed reason=remote_install_hook_failed pid=%lu export=%s ok=%u exit=%lu.",
                        static_cast<unsigned long>(req.pid),
                        installExport,
                        ok ? 1u : 0u,
                        static_cast<unsigned long>(installExit));
                }
            }

            const auto dllLeaf = ResolveAgentDllLeaf(req.api);
            if (dllLeaf == nullptr)
            {
                LogHost("event=attach_reject reason=api_not_implemented pid=%lu api=%s.", static_cast<unsigned long>(req.pid), ApiToLogString(req.api));
                WriteResponse(pipe, BuildState("Failed", "attach_failed:api_not_implemented", req.api, req.pid));
                return;
            }

            const auto exeDir = GetExeDir();
            const auto dllPath = JoinPath(exeDir, dllLeaf);

            std::string reason;
            HMODULE remoteModule = nullptr;
            const bool ok = InjectAgent(req.pid, req.api, dllPath, remoteModule, reason);
            if (!ok)
            {
                LogHost(
                    "event=attach_failed pid=%lu api=%s reason=%s dll=\"%ls\".",
                    static_cast<unsigned long>(req.pid),
                    ApiToLogString(req.api),
                    reason.c_str(),
                    dllPath.c_str());
                WriteResponse(pipe, BuildState("Failed", "attach_failed:" + reason, req.api, req.pid));
                return;
            }

            ProcessHookState st{};
            st.pid = req.pid;
            st.remoteModule = remoteModule;
            st.dllPath = dllPath;
            st.api = req.api;
            (void)st.configWriter.Write(
                req.pid,
                st.api,
                req.captureFpsLimit,
                req.enableOverlay,
                req.configFlags);
            g_states[req.pid] = std::move(st);

            LogHost(
                "event=attach_ok pid=%lu api=%s module=%p dll=\"%ls\".",
                static_cast<unsigned long>(req.pid),
                ApiToLogString(req.api),
                remoteModule,
                dllPath.c_str());
            WriteResponse(pipe, BuildState("Attached", "ok", req.api, req.pid));
            return;
        }

        if (message.find("\"type\":\"detach\"") != std::string::npos)
        {
            DWORD pid = 0;
            if (!ParseDetach(message, pid))
            {
                LogHost("event=detach_parse_failed.");
                WriteResponse(pipe, BuildState("Failed", "detach_parse_failed", ht::hook::ipc::GraphicsApi::Dx11, 0));
                return;
            }

            const auto it = g_states.find(pid);
            if (it == g_states.end())
            {
                LogHost("event=detach_skip reason=not_attached pid=%lu.", static_cast<unsigned long>(pid));
                WriteResponse(pipe, BuildState("Detached", "not_attached", ht::hook::ipc::GraphicsApi::Dx11, pid));
                return;
            }

            std::string reason;
            (void)UninstallAgent(it->second, reason);
            LogHost("event=detach_done pid=%lu reason=%s.", static_cast<unsigned long>(pid), reason.c_str());

            // WHY: With vtable patching, unloading the agent DLL would leave dangling function pointers in swapchain vtables.
            // We keep the module loaded for process lifetime in v1; detach only disables capture.
            it->second.configWriter.Reset();
            WriteResponse(pipe, BuildState("Detached", "ok_disabled", ht::hook::ipc::GraphicsApi::Dx11, pid));
            return;
        }

        if (message.find("\"type\":\"shutdown\"") != std::string::npos)
        {
            LogHost("event=shutdown_requested.");
            DisableAllHooksForShutdown();
            WriteResponse(pipe, BuildState("Stopped", "shutdown", ht::hook::ipc::GraphicsApi::Dx11, 0));
            g_shutdownRequested = true;
            return;
        }

        LogHost("event=unknown_message.");
        WriteResponse(pipe, BuildState("Running", "unknown_message", ht::hook::ipc::GraphicsApi::Dx11, 0));
    }
}

int wmain()
{
    std::wcout << L"[HookHost] starting pipe server: " << kPipeName << std::endl;
    LogHost("event=host_start pid=%lu pipe=\"%ls\".", static_cast<unsigned long>(GetCurrentProcessId()), kPipeName);
    const DWORD parentPid = GetParentProcessId(GetCurrentProcessId());
    LogHost("event=parent_detected pid=%lu parentPid=%lu.", static_cast<unsigned long>(GetCurrentProcessId()), static_cast<unsigned long>(parentPid));

    for (;;)
    {
        if (parentPid != 0 && !IsProcessAlive(parentPid))
        {
            DisableAllHooksForShutdown();
            std::wcout << L"[HookHost] parent process exited; shutting down." << std::endl;
            LogHost("event=host_shutdown reason=parent_exited.");
            CloseDiagFile();
            return 0;
        }

        HANDLE pipe = CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            kBufferBytes,
            kBufferBytes,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            std::cerr << "[HookHost] CreateNamedPipeW failed: " << GetLastError() << std::endl;
            LogHost("event=host_shutdown reason=create_named_pipe_failed gle=%lu.", static_cast<unsigned long>(GetLastError()));
            CloseDiagFile();
            return 1;
        }

        OVERLAPPED connectOv{};
        connectOv.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (connectOv.hEvent == nullptr)
        {
            CloseHandle(pipe);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, &connectOv);
        if (!connected)
        {
            const DWORD err = GetLastError();
            if (err == ERROR_PIPE_CONNECTED)
            {
                connected = TRUE;
            }
            else if (err == ERROR_IO_PENDING)
            {
                for (;;)
                {
                    const DWORD wait = WaitForSingleObject(connectOv.hEvent, 250);
                    if (wait == WAIT_OBJECT_0)
                    {
                        DWORD transferred = 0;
                        if (GetOverlappedResult(pipe, &connectOv, &transferred, FALSE) || GetLastError() == ERROR_PIPE_CONNECTED)
                        {
                            connected = TRUE;
                        }
                        else
                        {
                            connected = FALSE;
                        }
                        break;
                    }

                    if (wait == WAIT_TIMEOUT)
                    {
                        if (parentPid != 0 && !IsProcessAlive(parentPid))
                        {
                            DisableAllHooksForShutdown();
                            (void)CancelIoEx(pipe, &connectOv);
                            CloseHandle(connectOv.hEvent);
                            CloseHandle(pipe);
                            std::wcout << L"[HookHost] parent process exited; shutting down." << std::endl;
                            LogHost("event=host_shutdown reason=parent_exited_wait_connect.");
                            CloseDiagFile();
                            return 0;
                        }

                        continue;
                    }

                    connected = FALSE;
                    break;
                }
            }
        }

        CloseHandle(connectOv.hEvent);
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
                    if (g_shutdownRequested)
                    {
                        break;
                    }
                }
            }

            if (g_shutdownRequested)
            {
                break;
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        if (g_shutdownRequested)
        {
            break;
        }
    }

    LogHost("event=host_shutdown reason=requested.");
    CloseDiagFile();
    return 0;
}
