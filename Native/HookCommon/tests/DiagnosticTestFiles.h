#pragma once

#include <filesystem>
#include <fstream>
#include <iterator>

// WHY: Keep regression logs separate from game logs, including cases that must create no file.
struct DiagnosticTestFiles
{
    wchar_t previousTemp[32768]{};
    wchar_t previousTmp[32768]{};
    std::filesystem::path root;

    DiagnosticTestFiles()
    {
        GetEnvironmentVariableW(L"TEMP", previousTemp, 32768);
        GetEnvironmentVariableW(L"TMP", previousTmp, 32768);
        root = std::filesystem::current_path() /
            (L"diagnostic-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
        Require(std::filesystem::create_directory(root), "create isolated diagnostic directory");
        Require(SetEnvironmentVariableW(L"TEMP", root.c_str()) != FALSE, "redirect TEMP");
        Require(SetEnvironmentVariableW(L"TMP", root.c_str()) != FALSE, "redirect TMP");
    }

    ~DiagnosticTestFiles()
    {
        SetEnvironmentVariableW(L"TEMP", previousTemp[0] ? previousTemp : nullptr);
        SetEnvironmentVariableW(L"TMP", previousTmp[0] ? previousTmp : nullptr);
    }

    std::filesystem::path LogPath(const wchar_t* prefix) const
    {
        return root / L"HotkeyTranslator" / (std::wstring(prefix) + std::to_wstring(GetCurrentProcessId()) + L".log");
    }

    static std::string Read(const std::filesystem::path& path)
    {
        std::ifstream input(path, std::ios::binary);
        return std::string(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
    }
};
