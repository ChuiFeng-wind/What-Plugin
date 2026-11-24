// 查询插件里 Runner 的测试：不开 DAW，直接用它运行命令行工具，检查拿到的输出。
// 用法: RunnerTest <WhatPlugin.Cli 的路径> <测试数据库>
// 只读数据库（命令行工具的 lookup dump），不写任何文件，也不碰剪贴板。
#include "../source/Runner.h"

#include <chrono>
#include <cstdio>
#include <string>
#include <thread>

using namespace lookup;

namespace
{
int failures = 0;

void check (bool ok, const char* what)
{
    std::printf ("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (! ok)
        ++failures;
}

std::string waitFor (Runner& runner)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds (40);
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (auto result = runner.poll())
            return *result;
        std::this_thread::sleep_for (std::chrono::milliseconds (20));
    }
    return "<超时>";
}
} // namespace

int main (int argc, char** argv)
{
    if (argc < 3)
    {
        std::fprintf (stderr, "用法: RunnerTest <WhatPlugin.Cli> <数据库>\n");
        return 2;
    }
    const std::string cli = argv[1];
    const std::string db = argv[2];

    // 中文、全角空格和四字节字符来回转换不变。
    const std::string text = "压缩\xE3\x80\x80Pro-Q \xF0\x9F\x8E\xB5";
    check (narrow (widen (text)) == text, "UTF-8 来回转换");
    check (widen ("压缩").size() == 2, "中文按字符计数");

    check (fileExists (cli), "找得到命令行工具");
    check (! fileExists (cli + ".none"), "不存在的文件");

    Runner runner;
    runner.launch (cli, { "lookup", "dump", "--db", db });
    const auto dump = waitFor (runner);
    std::printf ("dump: %.300s\n", dump.c_str());
    check (dump.rfind ("{\"ok\":true", 0) == 0, "lookup dump 读到数据");

    // 参数里有空格和中文也原样传过去。
    runner.launch (cli, { "lookup", "recommend", "低延迟 压缩", "--db", db });
    const auto recommend = waitFor (runner);
    std::printf ("recommend: %.300s\n", recommend.c_str());
    // 需求被拆成“压缩、低延迟”写进 summary：两个词都到了，说明带空格的参数没有被拆坏。
    check (recommend.rfind ("{\"ok\":true", 0) == 0 && recommend.find ("低延迟") != std::string::npos
               && recommend.find ("压缩") != std::string::npos, "recommend 收到完整的需求");

    // 换一次命令：上一次的结果不会再出现。
    runner.launch (cli, { "lookup", "dump", "--db", db });
    runner.launch (cli, { "lookup", "dump", "--db", db });
    check (waitFor (runner).rfind ("{\"ok\":true", 0) == 0, "连续启动只取最后一次");
    runner.stop();
    check (! runner.poll().has_value(), "停下后没有结果");

    std::printf (failures == 0 ? "全部通过\n" : "%d 项失败\n", failures);
    return failures == 0 ? 0 : 1;
}
