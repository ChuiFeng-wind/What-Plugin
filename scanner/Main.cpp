// WhatPluginScanner: 在独立进程里读取一个 VST3 文件的插件清单，写成 JSON。
//
// 只加载模块并询问工厂有哪些插件，绝不创建插件实例（不调用 createInstance / initialize）。
// 插件实例初始化时可能打开摄像头、音频设备或授权窗口（如 Waves Nx 的头部追踪），
// 而名称、厂家、分类都可以直接从工厂读到。
//
// 每个文件一个进程，模块崩溃或卡死只影响这一次扫描，由调用方负责超时。
//
// 用法: WhatPluginScanner.exe <插件路径> <输出 JSON 路径>
// 退出码: 0 成功；2 文件里没有插件；3 参数错误；4 写结果失败；5 模块无法加载；6 模块没有工厂

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>

#define INIT_CLASS_IID
#include "pluginterfaces/base/ipluginbase.h"
#undef INIT_CLASS_IID

#include <juce_core/juce_core.h>

#pragma comment(lib, "ole32.lib")

namespace
{
    constexpr const char* audioEffectClass = "Audio Module Class";

    // 结果写完后直接结束进程：部分模块的卸载代码会崩溃或卡住，不能让它们影响已经拿到的结果。
    [[noreturn]] void exitNow (int code)
    {
        TerminateProcess (GetCurrentProcess(), (UINT) code);
        ExitProcess ((UINT) code);
    }

    juce::File resolveBinary (const juce::File& path)
    {
        if (! path.isDirectory())
            return path;

        const auto archDir = path.getChildFile ("Contents").getChildFile ("x86_64-win");
        const auto preferred = archDir.getChildFile (path.getFileName());
        if (preferred.existsAsFile())
            return preferred;

        const auto found = archDir.findChildFiles (juce::File::findFiles, false, "*.vst3");
        return found.isEmpty() ? juce::File() : found.getFirst();
    }

    juce::String fromUtf16 (const Steinberg::char16* text)
    {
        return juce::String (juce::CharPointer_UTF16 (reinterpret_cast<const juce::CharPointer_UTF16::CharType*> (text))).trim();
    }

    juce::var makeItem (const juce::String& name, const juce::String& vendor, const juce::String& version, const juce::String& category)
    {
        auto* o = new juce::DynamicObject();
        o->setProperty ("name", name);
        o->setProperty ("vendor", vendor);
        o->setProperty ("version", version);
        o->setProperty ("category", category);
        return juce::var (o);
    }
}

int wmain (int argc, wchar_t** argv)
{
    // 崩溃时不要弹系统错误窗口，让调用方通过退出码判断。
    SetErrorMode (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

    if (argc != 3)
        return 3;

    const juce::File pluginPath { juce::String (argv[1]) };
    const juce::File outputFile { juce::String (argv[2]) };

    CoInitializeEx (nullptr, COINIT_APARTMENTTHREADED);

    const auto binary = resolveBinary (pluginPath);
    if (! binary.existsAsFile())
        exitNow (5);

    // 让模块能找到放在它旁边的依赖 dll。
    auto* module = LoadLibraryExW (binary.getFullPathName().toWideCharPointer(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
        exitNow (5);

    using InitFn = bool (PLUGIN_API*)();
    using FactoryFn = Steinberg::IPluginFactory* (PLUGIN_API*)();

    if (auto init = reinterpret_cast<InitFn> (GetProcAddress (module, "InitDll")))
        init();

    auto getFactory = reinterpret_cast<FactoryFn> (GetProcAddress (module, "GetPluginFactory"));
    auto* factory = getFactory != nullptr ? getFactory() : nullptr;
    if (factory == nullptr)
        exitNow (6);

    Steinberg::PFactoryInfo factoryInfo {};
    factory->getFactoryInfo (&factoryInfo);
    const auto factoryVendor = juce::String (factoryInfo.vendor).trim();

    Steinberg::IPluginFactory2* factory2 = nullptr;
    Steinberg::IPluginFactory3* factory3 = nullptr;
    factory->queryInterface (Steinberg::IPluginFactory2::iid, reinterpret_cast<void**> (&factory2));
    factory->queryInterface (Steinberg::IPluginFactory3::iid, reinterpret_cast<void**> (&factory3));

    juce::Array<juce::var> items;
    juce::StringArray seenNames;

    const auto count = factory->countClasses();
    for (Steinberg::int32 i = 0; i < count; ++i)
    {
        Steinberg::PClassInfo info {};
        if (factory->getClassInfo (i, &info) != Steinberg::kResultOk
            || std::strcmp (info.category, audioEffectClass) != 0)
            continue;

        auto name = juce::String (info.name).trim();
        juce::String vendor, version, category;

        Steinberg::PClassInfo2 info2 {};
        if (factory2 != nullptr && factory2->getClassInfo2 (i, &info2) == Steinberg::kResultOk)
        {
            vendor = juce::String (info2.vendor).trim();
            version = juce::String (info2.version).trim();
            category = juce::String (info2.subCategories).trim();
        }

        // Unicode 版本的名称最准确（中文名、特殊字符）。
        Steinberg::PClassInfoW infoW {};
        if (factory3 != nullptr && factory3->getClassInfoUnicode (i, &infoW) == Steinberg::kResultOk)
        {
            if (const auto s = fromUtf16 (infoW.name); s.isNotEmpty())   name = s;
            if (const auto s = fromUtf16 (infoW.vendor); s.isNotEmpty()) vendor = s;
            if (const auto s = fromUtf16 (infoW.version); s.isNotEmpty()) version = s;
            if (const auto s = juce::String (infoW.subCategories).trim(); s.isNotEmpty()) category = s;
        }

        if (name.isEmpty() || seenNames.contains (name))
            continue;
        seenNames.add (name);

        items.add (makeItem (name, vendor.isNotEmpty() ? vendor : factoryVendor, version, category));
    }

    if (items.isEmpty())
        exitNow (2);

    if (! outputFile.replaceWithText (juce::JSON::toString (juce::var (items)), false, false, "\n"))
        exitNow (4);

    exitNow (0);
}
