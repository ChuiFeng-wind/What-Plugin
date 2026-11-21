// WhatPluginScanner: 在独立进程里读取一个 VST3 文件的插件清单，写成 JSON。
//
// 只加载模块并询问工厂有哪些插件，绝不创建插件实例（不调用 createInstance / initialize）。
// 插件实例初始化时可能打开摄像头、音频设备或授权窗口（如 Waves Nx 的头部追踪），
// 而名称、厂家、分类都可以直接从工厂读到。
//
// 每个文件一个进程，模块崩溃或卡死只影响这一次扫描，由调用方负责超时。
// 只依赖系统 API（Windows，或 macOS 的 CoreFoundation）、标准库和 VST3 SDK 的接口头文件。
//
// 用法: WhatPluginScanner(.exe) <插件路径> <输出 JSON 路径>
// 退出码: 0 成功；2 文件里没有插件；3 参数错误；4 写结果失败；5 模块无法加载；6 模块没有工厂

#ifdef _WIN32
 #include <windows.h>
 #include <objbase.h>
#else
 #include <CoreFoundation/CoreFoundation.h>
 #include <unistd.h>
#endif

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#define INIT_CLASS_IID
#include "pluginterfaces/base/ipluginbase.h"
#undef INIT_CLASS_IID

namespace fs = std::filesystem;

namespace
{
    constexpr const char* audioEffectClass = "Audio Module Class";

    using FactoryFn = Steinberg::IPluginFactory* (PLUGIN_API*)();

    /// UTF-16 转 UTF-8。不成对的代理项换成 U+FFFD。
    std::string utf16ToUtf8 (const char16_t* text, size_t length)
    {
        std::string out;
        out.reserve (length);
        for (size_t i = 0; i < length; ++i)
        {
            char32_t c = text[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < length && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF)
                c = 0x10000 + ((c - 0xD800) << 10) + (char32_t) (text[++i] - 0xDC00);
            else if (c >= 0xD800 && c <= 0xDFFF)
                c = 0xFFFD;

            if (c < 0x80)
            {
                out += (char) c;
            }
            else if (c < 0x800)
            {
                out += (char) (0xC0 | (c >> 6));
                out += (char) (0x80 | (c & 0x3F));
            }
            else if (c < 0x10000)
            {
                out += (char) (0xE0 | (c >> 12));
                out += (char) (0x80 | ((c >> 6) & 0x3F));
                out += (char) (0x80 | (c & 0x3F));
            }
            else
            {
                out += (char) (0xF0 | (c >> 18));
                out += (char) (0x80 | ((c >> 12) & 0x3F));
                out += (char) (0x80 | ((c >> 6) & 0x3F));
                out += (char) (0x80 | (c & 0x3F));
            }
        }
        return out;
    }

    std::string fromUtf16 (const Steinberg::char16* text, size_t capacity)
    {
        static_assert (sizeof (Steinberg::char16) == sizeof (char16_t));
        const auto* wide = reinterpret_cast<const char16_t*> (text);
        size_t length = 0;
        while (length < capacity && wide[length] != 0)
            ++length;
        return utf16ToUtf8 (wide, length);
    }

#ifdef _WIN32
    // 结果写完后直接结束进程：部分模块的卸载代码会崩溃或卡住，不能让它们影响已经拿到的结果。
    [[noreturn]] void exitNow (int code)
    {
        TerminateProcess (GetCurrentProcess(), (UINT) code);
        ExitProcess ((UINT) code);
    }

    /// 文件夹形式的 .vst3（Contents\x86_64-win\名称.vst3）取里面的二进制；找不到同名的就取第一个。
    fs::path resolveBinary (const fs::path& path)
    {
        std::error_code ec;
        if (! fs::is_directory (path, ec))
            return path;

        const auto archDir = path / L"Contents" / L"x86_64-win";
        const auto preferred = archDir / path.filename();
        if (fs::is_regular_file (preferred, ec))
            return preferred;

        std::vector<fs::path> found;
        for (fs::directory_iterator it (archDir, ec), end; ! ec && it != end; it.increment (ec))
            if (it->is_regular_file (ec) && _wcsicmp (it->path().extension().c_str(), L".vst3") == 0)
                found.push_back (it->path());
        std::sort (found.begin(), found.end());
        return found.empty() ? fs::path() : found.front();
    }

    std::string toUtf8 (const wchar_t* text, int length)
    {
        const auto size = WideCharToMultiByte (CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr);
        if (size <= 0)
            return {};
        std::string result ((size_t) size, '\0');
        WideCharToMultiByte (CP_UTF8, 0, text, length, result.data(), size, nullptr, nullptr);
        return result;
    }

    /// 工厂里的 char8 字段按规范是 UTF-8；个别老插件用的是系统代码页，不是合法 UTF-8 时按代码页转换。
    std::string fromChar8 (const char* text, size_t capacity)
    {
        const auto length = (int) strnlen (text, capacity);
        if (length == 0)
            return {};
        if (MultiByteToWideChar (CP_UTF8, MB_ERR_INVALID_CHARS, text, length, nullptr, 0) > 0)
            return std::string (text, (size_t) length);
        const auto size = MultiByteToWideChar (CP_ACP, 0, text, length, nullptr, 0);
        std::wstring wide ((size_t) size, L'\0');
        MultiByteToWideChar (CP_ACP, 0, text, length, wide.data(), size);
        return toUtf8 (wide.c_str(), size);
    }

    /// 用写好的临时文件替换结果文件，调用方不会读到写了一半的结果。
    bool replaceFile (const fs::path& temp, const fs::path& target)
    {
        return MoveFileExW (temp.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
    }

    /// 加载模块并取出工厂。模块加载不了时以 5 退出；没有工厂时返回 nullptr。
    Steinberg::IPluginFactory* loadFactory (const fs::path& pluginPath)
    {
        // 崩溃时不要弹系统错误窗口，让调用方通过退出码判断。
        SetErrorMode (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        CoInitializeEx (nullptr, COINIT_APARTMENTTHREADED);

        std::error_code ec;
        const auto binary = resolveBinary (pluginPath);
        if (binary.empty() || ! fs::is_regular_file (binary, ec))
            exitNow (5);

        // 让模块能找到放在它旁边的依赖 dll。
        auto* module = LoadLibraryExW (binary.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
            exitNow (5);

        using InitFn = bool (PLUGIN_API*)();
        if (auto init = reinterpret_cast<InitFn> (GetProcAddress (module, "InitDll")))
            init();

        auto getFactory = reinterpret_cast<FactoryFn> (GetProcAddress (module, "GetPluginFactory"));
        return getFactory != nullptr ? getFactory() : nullptr;
    }
#else
    // 结果写完后直接结束进程，不运行插件的卸载代码（理由同 Windows 版）。
    [[noreturn]] void exitNow (int code)
    {
        _exit (code);
    }

    /// 只检查结构：每个多字节字符的后续字节都是 10xxxxxx。
    bool validUtf8 (const unsigned char* text, size_t length)
    {
        for (size_t i = 0; i < length;)
        {
            const auto c = text[i];
            size_t extra;
            if (c < 0x80)
                extra = 0;
            else if ((c & 0xE0) == 0xC0)
                extra = 1;
            else if ((c & 0xF0) == 0xE0)
                extra = 2;
            else if ((c & 0xF8) == 0xF0)
                extra = 3;
            else
                return false;
            if (i + extra >= length)
                return false;
            for (size_t k = 1; k <= extra; ++k)
                if ((text[i + k] & 0xC0) != 0x80)
                    return false;
            i += extra + 1;
        }
        return true;
    }

    /// 工厂里的 char8 字段按规范是 UTF-8；不是合法 UTF-8 的老插件多半用 Mac Roman，按它转换。
    std::string fromChar8 (const char* text, size_t capacity)
    {
        const auto length = strnlen (text, capacity);
        if (length == 0)
            return {};
        if (validUtf8 (reinterpret_cast<const unsigned char*> (text), length))
            return std::string (text, length);

        std::string out;
        if (auto str = CFStringCreateWithBytes (nullptr, reinterpret_cast<const UInt8*> (text), (CFIndex) length, kCFStringEncodingMacRoman, false))
        {
            const auto max = CFStringGetMaximumSizeForEncoding (CFStringGetLength (str), kCFStringEncodingUTF8) + 1;
            std::vector<char> buffer ((size_t) max);
            if (CFStringGetCString (str, buffer.data(), max, kCFStringEncodingUTF8))
                out = buffer.data();
            CFRelease (str);
        }
        return out;
    }

    /// 同一目录内 rename 是原子的，调用方不会读到写了一半的结果。
    bool replaceFile (const fs::path& temp, const fs::path& target)
    {
        std::error_code ec;
        fs::rename (temp, target, ec);
        return ! ec;
    }

    /// 按 VST3 在 Mac 上的约定加载插件包：先调 bundleEntry(插件包)，再取工厂。
    /// 模块加载不了时以 5 退出；没有工厂时返回 nullptr。
    Steinberg::IPluginFactory* loadFactory (const fs::path& pluginPath)
    {
        std::error_code ec;
        if (! fs::is_directory (pluginPath, ec))
            exitNow (5);

        const auto& native = pluginPath.native();
        auto url = CFURLCreateFromFileSystemRepresentation (nullptr, reinterpret_cast<const UInt8*> (native.c_str()), (CFIndex) native.size(), true);
        if (url == nullptr)
            exitNow (5);
        auto bundle = CFBundleCreate (nullptr, url);
        CFRelease (url);
        // 不释放 bundle：模块在整个进程里都要保持加载。
        if (bundle == nullptr || ! CFBundleLoadExecutable (bundle))
            exitNow (5);

        using EntryFn = bool (*)(CFBundleRef);
        if (auto entry = reinterpret_cast<EntryFn> (CFBundleGetFunctionPointerForName (bundle, CFSTR ("bundleEntry"))))
            if (! entry (bundle))
                exitNow (5);

        auto getFactory = reinterpret_cast<FactoryFn> (CFBundleGetFunctionPointerForName (bundle, CFSTR ("GetPluginFactory")));
        return getFactory != nullptr ? getFactory() : nullptr;
    }
#endif

    std::string trim (std::string s)
    {
        const auto isSpace = [] (unsigned char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'; };
        while (! s.empty() && isSpace ((unsigned char) s.back()))
            s.pop_back();
        size_t start = 0;
        while (start < s.size() && isSpace ((unsigned char) s[start]))
            ++start;
        return s.substr (start);
    }

    template <size_t N> std::string field (const char (&text)[N]) { return trim (fromChar8 (text, N)); }
    template <size_t N> std::string field (const Steinberg::char16 (&text)[N]) { return trim (fromUtf16 (text, N)); }

    void appendJsonString (std::string& out, const std::string& text)
    {
        out += '"';
        for (const unsigned char c : text)
        {
            switch (c)
            {
                case '"':  out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n"; break;
                case '\r': out += "\\r"; break;
                case '\t': out += "\\t"; break;
                default:
                    if (c < 0x20)
                    {
                        char buffer[8];
                        snprintf (buffer, sizeof buffer, "\\u%04x", c);
                        out += buffer;
                    }
                    else
                    {
                        out += (char) c; // UTF-8 原样写出
                    }
            }
        }
        out += '"';
    }

    struct Item { std::string name, vendor, version, category; };

    std::string toJson (const std::vector<Item>& items)
    {
        std::string out = "[\n";
        for (size_t i = 0; i < items.size(); ++i)
        {
            const auto& item = items[i];
            out += "  {\n    \"name\": ";     appendJsonString (out, item.name);
            out += ",\n    \"vendor\": ";     appendJsonString (out, item.vendor);
            out += ",\n    \"version\": ";    appendJsonString (out, item.version);
            out += ",\n    \"category\": ";   appendJsonString (out, item.category);
            out += i + 1 < items.size() ? "\n  },\n" : "\n  }\n";
        }
        out += "]\n";
        return out;
    }

    /// 先写临时文件再替换。UTF-8，无 BOM。
    bool writeFile (const fs::path& target, const std::string& text)
    {
        auto temp = target;
        temp += ".tmp";
        {
            std::ofstream file (temp, std::ios::binary | std::ios::trunc);
            if (! file)
                return false;
            file.write (text.data(), (std::streamsize) text.size());
            if (! file)
                return false;
        }
        return replaceFile (temp, target);
    }

    int run (const fs::path& pluginArg, const fs::path& outputArg)
    {
        std::error_code ec;
        const fs::path pluginPath = fs::absolute (pluginArg, ec);
        const fs::path outputFile = fs::absolute (outputArg, ec);

        auto* factory = loadFactory (pluginPath);
        if (factory == nullptr)
            exitNow (6);

        Steinberg::PFactoryInfo factoryInfo {};
        factory->getFactoryInfo (&factoryInfo);
        const auto factoryVendor = field (factoryInfo.vendor);

        Steinberg::IPluginFactory2* factory2 = nullptr;
        Steinberg::IPluginFactory3* factory3 = nullptr;
        factory->queryInterface (Steinberg::IPluginFactory2::iid, reinterpret_cast<void**> (&factory2));
        factory->queryInterface (Steinberg::IPluginFactory3::iid, reinterpret_cast<void**> (&factory3));

        std::vector<Item> items;
        std::vector<std::string> seenNames;

        const auto count = factory->countClasses();
        for (Steinberg::int32 i = 0; i < count; ++i)
        {
            Steinberg::PClassInfo info {};
            if (factory->getClassInfo (i, &info) != Steinberg::kResultOk
                || strncmp (info.category, audioEffectClass, sizeof info.category) != 0)
                continue;

            Item item { field (info.name), {}, {}, {} };

            Steinberg::PClassInfo2 info2 {};
            if (factory2 != nullptr && factory2->getClassInfo2 (i, &info2) == Steinberg::kResultOk)
            {
                item.vendor = field (info2.vendor);
                item.version = field (info2.version);
                item.category = field (info2.subCategories);
            }

            // Unicode 版本的名称最准确（中文名、特殊字符）。
            Steinberg::PClassInfoW infoW {};
            if (factory3 != nullptr && factory3->getClassInfoUnicode (i, &infoW) == Steinberg::kResultOk)
            {
                if (auto s = field (infoW.name); ! s.empty())          item.name = s;
                if (auto s = field (infoW.vendor); ! s.empty())        item.vendor = s;
                if (auto s = field (infoW.version); ! s.empty())       item.version = s;
                if (auto s = field (infoW.subCategories); ! s.empty()) item.category = s;
            }

            if (item.name.empty() || std::find (seenNames.begin(), seenNames.end(), item.name) != seenNames.end())
                continue;
            seenNames.push_back (item.name);

            if (item.vendor.empty())
                item.vendor = factoryVendor;
            items.push_back (std::move (item));
        }

        if (items.empty())
            exitNow (2);

        if (! writeFile (outputFile, toJson (items)))
            exitNow (4);

        exitNow (0);
    }
}

#ifdef _WIN32
int wmain (int argc, wchar_t** argv)
{
    if (argc != 3)
        return 3;
    return run (argv[1], argv[2]);
}
#else
int main (int argc, char** argv)
{
    if (argc != 3)
        return 3;
    return run (argv[1], argv[2]);
}
#endif
