// 在后台线程里运行 WhatPlugin 的命令行工具，拿到它的标准输出。
// 界面线程用定时器调用 poll() 取结果，不在后台线程里碰界面。
#pragma once

#include <atomic>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

namespace lookup
{
class Runner
{
public:
    Runner() = default;
    ~Runner(); // 一定会停下线程：插件被卸载时线程还在跑会让 DAW 崩溃。

    Runner (const Runner&) = delete;
    Runner& operator= (const Runner&) = delete;

    /// 停掉上一次的命令，再在后台运行这一次的。exe 和参数都是 UTF-8。
    void launch (const std::string& exe, const std::vector<std::string>& args);

    /// 有新结果时返回输出（UTF-8），否则为空。只在界面线程调用。
    std::optional<std::string> poll();

    void stop();

private:
    void run (std::vector<std::string> argv, unsigned generation);

    std::thread worker;
    std::atomic<bool> cancel { false };
    std::atomic<unsigned> currentGeneration { 0 };
    std::mutex lock;
    std::optional<std::string> result;
    void* process = nullptr; // Windows：进程句柄，受 lock 保护
    int pid = -1;            // Mac：进程号，受 lock 保护
};

/// 命令行工具的位置：环境变量 WHATPLUGIN_LOOKUP_CLI（测试用），否则读主程序写下的 cli-path.txt
/// （Windows 在 %LOCALAPPDATA%\WhatPlugin，Mac 在 ~/Library/Application Support/WhatPlugin）。找不到时返回空。
std::string findCli();

/// 测试用：环境变量 WHATPLUGIN_LOOKUP_DB 指定数据库。
std::string testDatabase();

bool fileExists (const std::string& utf8Path);

/// 上次用的配色（主程序或上一次读取时保存的 JSON），打开窗口时先用它，不等读取完成。
std::string cachedTheme();
void saveCachedTheme (const std::string& json);

/// 把文字放到系统剪贴板（Unicode）。
bool copyToClipboard (const std::string& utf8);

std::wstring widen (const std::string& utf8);
std::string narrow (const std::wstring& wide);
} // namespace lookup
