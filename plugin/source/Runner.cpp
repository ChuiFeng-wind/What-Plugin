#include "Runner.h"

#if defined(_WIN32)
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <cerrno>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <fcntl.h>
#include <poll.h>
#include <spawn.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>
extern char** environ;
#endif

#if defined(__APPLE__)
#include <objc/message.h>
#include <objc/runtime.h>
// 自动释放池的接口在运行时库里，公开头文件没有声明。
extern "C" void* objc_autoreleasePoolPush (void);
extern "C" void objc_autoreleasePoolPop (void* pool);
#endif

#include <algorithm>
#include <chrono>
#include <cstring>
#include <fstream>
#include <sstream>

namespace lookup
{
// ---------- 文字编码 ----------

#if defined(_WIN32)
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
#else
// Mac 上 wchar_t 是 32 位，一个字符就是一个码点。坏掉的字节换成 U+FFFD。
std::wstring widen (const std::string& utf8)
{
    std::wstring wide;
    for (size_t i = 0; i < utf8.size();)
    {
        const auto c = (unsigned char) utf8[i];
        const int extra = c < 0x80 ? 0 : (c >> 5) == 0x6 ? 1 : (c >> 4) == 0xE ? 2 : (c >> 3) == 0x1E ? 3 : -1;
        // 后面跟的字节不够也算坏字节。
        if (extra < 0 || i + (size_t) extra >= utf8.size())
        {
            wide += (wchar_t) 0xFFFD;
            ++i;
            continue;
        }
        char32_t code = extra == 0 ? c : extra == 1 ? (c & 0x1F) : extra == 2 ? (c & 0x0F) : (c & 0x07);
        bool ok = true;
        for (int k = 1; k <= extra; ++k)
        {
            const auto next = (unsigned char) utf8[i + (size_t) k];
            if ((next & 0xC0) != 0x80)
            {
                ok = false;
                break;
            }
            code = (code << 6) | (next & 0x3F);
        }
        wide += ok ? (wchar_t) code : (wchar_t) 0xFFFD;
        i += ok ? (size_t) extra + 1 : 1;
    }
    return wide;
}

std::string narrow (const std::wstring& wide)
{
    std::string utf8;
    for (const auto ch : wide)
    {
        const auto code = (char32_t) ch;
        if (code < 0x80)
            utf8 += (char) code;
        else if (code < 0x800)
        {
            utf8 += (char) (0xC0 | (code >> 6));
            utf8 += (char) (0x80 | (code & 0x3F));
        }
        else if (code < 0x10000)
        {
            utf8 += (char) (0xE0 | (code >> 12));
            utf8 += (char) (0x80 | ((code >> 6) & 0x3F));
            utf8 += (char) (0x80 | (code & 0x3F));
        }
        else
        {
            utf8 += (char) (0xF0 | (code >> 18));
            utf8 += (char) (0x80 | ((code >> 12) & 0x3F));
            utf8 += (char) (0x80 | ((code >> 6) & 0x3F));
            utf8 += (char) (0x80 | (code & 0x3F));
        }
    }
    return utf8;
}
#endif

namespace
{
std::string environment (const char* name)
{
#if defined(_WIN32)
    wchar_t buffer[4096];
    const auto length = GetEnvironmentVariableW (widen (name).c_str(), buffer, 4096);
    return length > 0 && length < 4096 ? narrow (std::wstring (buffer, length)) : std::string();
#else
    const char* value = std::getenv (name);
    return value != nullptr ? std::string (value) : std::string();
#endif
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

#if defined(_WIN32)
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
#endif

/// 主程序给查询插件留文件的文件夹（UTF-8，末尾带分隔符）。和主程序的数据文件夹是同一个。
std::string sharedFolder()
{
#if defined(_WIN32)
    const auto localAppData = environment ("LOCALAPPDATA");
    return localAppData.empty() ? std::string() : localAppData + "\\WhatPlugin\\";
#else
    const auto home = environment ("HOME");
    return home.empty() ? std::string() : home + "/Library/Application Support/WhatPlugin/";
#endif
}

#if defined(_WIN32)
using NativePath = std::wstring;
NativePath native (const std::string& utf8) { return widen (utf8); }
#else
using NativePath = std::string;
NativePath native (const std::string& utf8) { return utf8; }
#endif

std::string sharedFile (const char* name)
{
    const auto folder = sharedFolder();
    return folder.empty() ? std::string() : folder + name;
}

std::string readSharedFile (const char* name)
{
    const auto path = sharedFile (name);
    if (path.empty())
        return {};
    std::ifstream file (native (path), std::ios::binary);
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
    if (auto fromEnv = environment ("WHATPLUGIN_LOOKUP_CLI"); ! fromEnv.empty())
        return fromEnv;
    return trim (readSharedFile ("cli-path.txt"));
}

std::string testDatabase() { return environment ("WHATPLUGIN_LOOKUP_DB"); }

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
    const auto temp = path + ".tmp";
    {
        std::ofstream file (native (temp), std::ios::binary | std::ios::trunc);
        if (! file)
            return;
        file << json;
        if (! file)
            return;
    }
#if defined(_WIN32)
    MoveFileExW (widen (temp).c_str(), widen (path).c_str(), MOVEFILE_REPLACE_EXISTING);
#else
    std::rename (temp.c_str(), path.c_str());
#endif
}

bool copyToClipboard (const std::string& utf8)
{
#if defined(_WIN32)
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
#elif defined(__APPLE__)
    // [[NSPasteboard generalPasteboard] setString:... forType:NSPasteboardTypeString]，不需要 Objective-C 源文件。
    using Send = id (*) (id, SEL);
    using SendText = id (*) (id, SEL, const char*);
    using SendSet = BOOL (*) (id, SEL, id, id);
    auto* pool = objc_autoreleasePoolPush();
    const auto stringClass = (id) objc_getClass ("NSString");
    const auto board = ((Send) objc_msgSend) ((id) objc_getClass ("NSPasteboard"), sel_registerName ("generalPasteboard"));
    bool ok = false;
    if (board != nullptr && stringClass != nullptr)
    {
        ((Send) objc_msgSend) (board, sel_registerName ("clearContents"));
        const auto text = ((SendText) objc_msgSend) (stringClass, sel_registerName ("stringWithUTF8String:"), utf8.c_str());
        const auto type = ((SendText) objc_msgSend) (stringClass, sel_registerName ("stringWithUTF8String:"), "public.utf8-plain-text");
        ok = text != nullptr && ((SendSet) objc_msgSend) (board, sel_registerName ("setString:forType:"), text, type);
    }
    objc_autoreleasePoolPop (pool);
    return ok;
#else
    (void) utf8;
    return false;
#endif
}

bool fileExists (const std::string& utf8Path)
{
    if (utf8Path.empty())
        return false;
#if defined(_WIN32)
    const auto attributes = GetFileAttributesW (widen (utf8Path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && ! (attributes & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat info {};
    return ::stat (utf8Path.c_str(), &info) == 0 && S_ISREG (info.st_mode);
#endif
}

Runner::~Runner() { stop(); }

void Runner::stop()
{
    cancel = true;
    {
        std::lock_guard<std::mutex> guard (lock);
#if defined(_WIN32)
        if (process != nullptr)
            TerminateProcess ((HANDLE) process, 1);
#else
        // pid 在 run 里持锁回收后才清掉，这里不会误杀别的进程。
        if (pid > 0)
            ::kill (pid, SIGKILL);
#endif
    }
    if (worker.joinable())
        worker.join();
    cancel = false;
}

void Runner::launch (const std::string& exe, const std::vector<std::string>& args)
{
    stop();
    std::vector<std::string> argv { exe };
    argv.insert (argv.end(), args.begin(), args.end());
    const auto generation = ++currentGeneration;
    {
        std::lock_guard<std::mutex> guard (lock);
        result.reset();
    }
    worker = std::thread ([this, argv, generation] { run (argv, generation); });
}

std::optional<std::string> Runner::poll()
{
    std::lock_guard<std::mutex> guard (lock);
    return std::exchange (result, std::nullopt);
}

#if defined(_WIN32)
void Runner::run (std::vector<std::string> argv, unsigned generation)
{
    std::wstring commandLine;
    for (const auto& arg : argv)
    {
        if (! commandLine.empty())
            commandLine += L' ';
        appendQuoted (commandLine, widen (arg));
    }

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
#else
void Runner::run (std::vector<std::string> argv, unsigned generation)
{
    std::string output;
    int fds[2] = { -1, -1 };
    if (::pipe (fds) == 0)
    {
        // 读的一端不留给子进程。
        ::fcntl (fds[0], F_SETFD, FD_CLOEXEC);

        posix_spawn_file_actions_t actions;
        posix_spawn_file_actions_init (&actions);
        posix_spawn_file_actions_adddup2 (&actions, fds[1], STDOUT_FILENO);
        posix_spawn_file_actions_addclose (&actions, fds[1]);
        posix_spawn_file_actions_addopen (&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0);

        std::vector<char*> args;
        for (auto& arg : argv)
            args.push_back (arg.data());
        args.push_back (nullptr);

        // posix_spawn 在多线程的宿主进程里也是安全的（不像 fork 后再做别的事）。
        pid_t child = -1;
        const auto spawned = posix_spawn (&child, argv[0].c_str(), &actions, nullptr, args.data(), environ) == 0;
        posix_spawn_file_actions_destroy (&actions);
        ::close (fds[1]);

        if (spawned)
        {
            {
                std::lock_guard<std::mutex> guard (lock);
                pid = child;
            }
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds (30);
            char buffer[8192];
            while (! cancel && std::chrono::steady_clock::now() < deadline)
            {
                pollfd ready { fds[0], POLLIN, 0 };
                const auto events = ::poll (&ready, 1, 10);
                if (events < 0 && errno != EINTR)
                    break;
                if (events <= 0)
                    continue;
                const auto got = ::read (fds[0], buffer, sizeof buffer);
                if (got < 0 && errno == EINTR)
                    continue;
                if (got <= 0)
                    break; // 子进程退出，管道关闭
                output.append (buffer, (size_t) got);
            }

            {
                std::lock_guard<std::mutex> guard (lock);
                int status = 0;
                if (::waitpid (child, &status, WNOHANG) == 0)
                {
                    ::kill (child, SIGKILL);
                    ::waitpid (child, &status, 0);
                }
                pid = -1;
            }
        }
        ::close (fds[0]);
    }

    if (cancel || generation != currentGeneration)
        return;
    std::lock_guard<std::mutex> guard (lock);
    result = std::move (output);
}
#endif
} // namespace lookup
