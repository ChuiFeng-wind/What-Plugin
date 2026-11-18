#include "Runner.h"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <fstream>
#include <sstream>

namespace lookup
{
std::wstring widen (const std::string& utf8)
{
    if (utf8.empty())
        return {};
    const auto size = MultiByteToWideChar (CP_UTF8, 0, utf8.data(), (int) utf8.size(), nullptr, 0);
    std::wstring wide ((size_t) size, L'\0');
    MultiByteToWideChar (CP_UTF8, 0, utf8.data(), (int) utf8.size(), wide.data(), size);
    return wide;
}

std::string narrow (const std::wstring& wide)
{
    if (wide.empty())
        return {};
    const auto size = WideCharToMultiByte (CP_UTF8, 0, wide.data(), (int) wide.size(), nullptr, 0, nullptr, nullptr);
    std::string utf8 ((size_t) size, '\0');
    WideCharToMultiByte (CP_UTF8, 0, wide.data(), (int) wide.size(), utf8.data(), size, nullptr, nullptr);
    return utf8;
}

namespace
{
std::string environment (const wchar_t* name)
{
    wchar_t buffer[4096];
    const auto length = GetEnvironmentVariableW (name, buffer, 4096);
    return length > 0 && length < 4096 ? narrow (std::wstring (buffer, length)) : std::string();
}

std::string trim (std::string s)
{
    const auto space = [] (char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; };
    while (! s.empty() && space (s.back()))
        s.pop_back();
    size_t start = 0;
    // UTF-8 BOM
    if (s.compare (0, 3, "\xEF\xBB\xBF") == 0)
        start = 3;
    while (start < s.size() && space (s[start]))
        ++start;
    return s.substr (start);
}

/// Windows 命令行的引号规则：参数整体加引号，引号前的反斜杠要加倍。
void appendQuoted (std::wstring& line, const std::wstring& arg)
{
    line += L'"';
    size_t backslashes = 0;
    for (const auto c : arg)
    {
        if (c == L'\\')
        {
            ++backslashes;
            continue;
        }
        if (c == L'"')
            line.append (backslashes * 2 + 1, L'\\');
        else
            line.append (backslashes, L'\\');
        backslashes = 0;
        line += c;
    }
    line.append (backslashes * 2, L'\\');
    line += L'"';
}
} // namespace

namespace
{
/// 主程序给查询插件留的文件：%LOCALAPPDATA%\WhatPlugin\<name>。
std::wstring sharedFile (const char* name)
{
    const auto localAppData = environment (L"LOCALAPPDATA");
    return localAppData.empty() ? std::wstring() : widen (localAppData + "\\WhatPlugin\\" + name);
}

std::string readSharedFile (const char* name)
{
    const auto path = sharedFile (name);
    if (path.empty())
        return {};
    std::ifstream file (path, std::ios::binary);
    if (! file)
        return {};
    std::stringstream content;
    content << file.rdbuf();
    return content.str();
}

const char* const themeFile = "lookup-theme.json";
} // namespace

std::string findCli()
{
    if (auto fromEnv = environment (L"WHATPLUGIN_LOOKUP_CLI"); ! fromEnv.empty())
        return fromEnv;
    return trim (readSharedFile ("cli-path.txt"));
}

std::string testDatabase() { return environment (L"WHATPLUGIN_LOOKUP_DB"); }

std::string cachedTheme()
{
    // 用测试数据库时不用本机的配色，也不改它。
    return testDatabase().empty() ? readSharedFile (themeFile) : std::string();
}

void saveCachedTheme (const std::string& json)
{
    if (! testDatabase().empty() || json.empty() || json == readSharedFile (themeFile))
        return;
    const auto path = sharedFile (themeFile);
    if (path.empty())
        return;
    // 先写临时文件再替换，另一个实例同时读时不会读到半截。
    const auto temp = path + L".tmp";
    {
        std::ofstream file (temp, std::ios::binary | std::ios::trunc);
        if (! file)
            return;
        file << json;
        if (! file)
            return;
    }
    MoveFileExW (temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
}

bool copyToClipboard (const std::string& utf8)
{
    const auto text = widen (utf8);
    const auto bytes = (text.size() + 1) * sizeof (wchar_t);
    HGLOBAL memory = GlobalAlloc (GMEM_MOVEABLE, bytes);
    if (memory == nullptr)
        return false;
    if (auto* target = GlobalLock (memory))
    {
        std::memcpy (target, text.c_str(), bytes); // 连同结尾的 0
        GlobalUnlock (memory);
    }
    if (! OpenClipboard (nullptr))
    {
        GlobalFree (memory);
        return false;
    }
    EmptyClipboard();
    const auto ok = SetClipboardData (CF_UNICODETEXT, memory) != nullptr;
    CloseClipboard();
    if (! ok)
        GlobalFree (memory);
    return ok;
}

bool fileExists (const std::string& utf8Path)
{
    if (utf8Path.empty())
        return false;
    const auto attributes = GetFileAttributesW (widen (utf8Path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && ! (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

Runner::~Runner() { stop(); }

void Runner::stop()
{
    cancel = true;
    {
        std::lock_guard<std::mutex> guard (lock);
        if (process != nullptr)
            TerminateProcess ((HANDLE) process, 1);
    }
    if (worker.joinable())
        worker.join();
    cancel = false;
}

void Runner::launch (const std::string& exe, const std::vector<std::string>& args)
{
    stop();
    std::wstring line;
    appendQuoted (line, widen (exe));
    for (const auto& arg : args)
    {
        line += L' ';
        appendQuoted (line, widen (arg));
    }
    const auto generation = ++currentGeneration;
    {
        std::lock_guard<std::mutex> guard (lock);
        result.reset();
    }
    worker = std::thread ([this, line, generation] { run (line, generation); });
}

std::optional<std::string> Runner::poll()
{
    std::lock_guard<std::mutex> guard (lock);
    return std::exchange (result, std::nullopt);
}

void Runner::run (std::wstring commandLine, unsigned generation)
{
    SECURITY_ATTRIBUTES security { sizeof (SECURITY_ATTRIBUTES), nullptr, TRUE };
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    std::string output;
    if (CreatePipe (&readEnd, &writeEnd, &security, 0))
    {
        // 读的一端不能被子进程继承，否则子进程退出后 ReadFile 不会结束。
        SetHandleInformation (readEnd, HANDLE_FLAG_INHERIT, 0);

        STARTUPINFOW startup {};
        startup.cb = sizeof startup;
        startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        startup.hStdOutput = writeEnd;
        startup.hStdError = GetStdHandle (STD_ERROR_HANDLE);
        startup.hStdInput = nullptr;

        PROCESS_INFORMATION info {};
        // CREATE_NO_WINDOW：命令行工具是控制台程序，不能闪出黑窗口。
        if (CreateProcessW (nullptr, commandLine.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                            nullptr, nullptr, &startup, &info))
        {
            CloseHandle (info.hThread);
            CloseHandle (writeEnd);
            writeEnd = nullptr;
            {
                std::lock_guard<std::mutex> guard (lock);
                process = info.hProcess;
            }

            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds (30);
            char buffer[8192];
            while (! cancel && std::chrono::steady_clock::now() < deadline)
            {
                DWORD available = 0;
                if (! PeekNamedPipe (readEnd, nullptr, 0, nullptr, &available, nullptr))
                    break; // 子进程退出，管道关闭
                if (available == 0)
                {
                    if (WaitForSingleObject (info.hProcess, 10) == WAIT_OBJECT_0)
                    {
                        // 退出了，把剩下的读完。
                        if (! PeekNamedPipe (readEnd, nullptr, 0, nullptr, &available, nullptr) || available == 0)
                            break;
                    }
                    else
                        continue;
                }
                DWORD got = 0;
                if (! ReadFile (readEnd, buffer, (DWORD) std::min<size_t> (sizeof buffer, available), &got, nullptr) || got == 0)
                    break;
                output.append (buffer, got);
            }

            {
                std::lock_guard<std::mutex> guard (lock);
                if (WaitForSingleObject (info.hProcess, 0) != WAIT_OBJECT_0)
                    TerminateProcess (info.hProcess, 1);
                CloseHandle (info.hProcess);
                process = nullptr;
            }
        }
        if (writeEnd != nullptr)
            CloseHandle (writeEnd);
        CloseHandle (readEnd);
    }

    if (cancel || generation != currentGeneration)
        return;
    std::lock_guard<std::mutex> guard (lock);
    result = std::move (output);
}
} // namespace lookup
