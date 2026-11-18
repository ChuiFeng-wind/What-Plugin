// 测试用的最小宿主：加载 WhatPluginLookup，把界面开在屏幕外、不激活，按脚本操作并截图。
// 不会打断正在用电脑的人，也不碰 DAW。只在开发时编译（-DWHATPLUGIN_BUILD_TEST_HOST=ON）。
//
// 用法: LookupHost.exe <插件 .vst3 路径> <脚本文件>
// 脚本每行一条命令（UTF-8）：
//   wait 毫秒 | click x y | dblclick x y | type 文字 | key return|escape|down|up
//   shot 文件.png | resize 宽 高 | reopen | log 文字
// 坐标是界面里的逻辑坐标（左上角为 0,0）。

#define NOMINMAX
#include <windows.h>
#include <gdiplus.h>

#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"

#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

#pragma comment(lib, "gdiplus.lib")

using namespace Steinberg;

namespace
{
HWND window = nullptr;

std::wstring widen (const std::string& s)
{
    const auto n = MultiByteToWideChar (CP_UTF8, 0, s.data(), (int) s.size(), nullptr, 0);
    std::wstring w ((size_t) n, L'\0');
    MultiByteToWideChar (CP_UTF8, 0, s.data(), (int) s.size(), w.data(), n);
    return w;
}

void pump (DWORD ms)
{
    const auto end = GetTickCount64() + ms;
    do
    {
        MSG msg;
        while (PeekMessageW (&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage (&msg);
            DispatchMessageW (&msg);
        }
        Sleep (5);
    } while (GetTickCount64() < end);
}

/// 点到的最深的子窗口，以及点在它里面的坐标。
HWND childAt (POINT& pt)
{
    HWND current = window;
    while (true)
    {
        HWND child = ChildWindowFromPointEx (current, pt, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
        if (child == nullptr || child == current)
            return current;
        MapWindowPoints (current, child, &pt, 1);
        current = child;
    }
}

void click (int x, int y, bool dbl)
{
    POINT pt { x, y };
    HWND target = childAt (pt);
    const auto lp = MAKELPARAM (pt.x, pt.y);
    SendMessageW (target, WM_MOUSEMOVE, 0, lp);
    SendMessageW (target, WM_LBUTTONDOWN, MK_LBUTTON, lp);
    SendMessageW (target, WM_LBUTTONUP, 0, lp);
    if (dbl)
    {
        SendMessageW (target, WM_LBUTTONDBLCLK, MK_LBUTTON, lp);
        SendMessageW (target, WM_LBUTTONUP, 0, lp);
    }
}

HWND findEdit()
{
    struct Finder { HWND found = nullptr; } finder;
    EnumChildWindows (window, [] (HWND h, LPARAM p) -> BOOL {
        wchar_t name[64];
        GetClassNameW (h, name, 64);
        if (_wcsicmp (name, L"EDIT") == 0)
        {
            reinterpret_cast<Finder*> (p)->found = h;
            return FALSE;
        }
        return TRUE;
    }, (LPARAM) &finder);
    return finder.found;
}

/// 有原生输入框时发给它，否则发给界面。
HWND keyTarget()
{
    if (auto edit = findEdit())
        return edit;
    POINT pt { 10, 10 };
    return childAt (pt);
}

void key (const std::string& name)
{
    const WPARAM vk = name == "return" ? VK_RETURN : name == "escape" ? VK_ESCAPE : name == "down" ? VK_DOWN : VK_UP;
    HWND target = keyTarget();
    SendMessageW (target, WM_KEYDOWN, vk, 1);
    if (vk == VK_RETURN || vk == VK_ESCAPE)
        SendMessageW (target, WM_CHAR, vk == VK_RETURN ? L'\r' : 0x1B, 1);
    if (IsWindow (target))
        SendMessageW (target, WM_KEYUP, vk, 0xC0000001);
}

void type (const std::string& text)
{
    HWND edit = findEdit();
    if (edit == nullptr)
    {
        std::cout << "!! no edit control" << std::endl;
        return;
    }
    for (const auto c : widen (text))
        SendMessageW (edit, WM_CHAR, c, 1);
}

int encoderClsid (const WCHAR* format, CLSID* clsid)
{
    UINT num = 0, size = 0;
    Gdiplus::GetImageEncodersSize (&num, &size);
    std::string buffer (size, '\0');
    auto* codecs = reinterpret_cast<Gdiplus::ImageCodecInfo*> (buffer.data());
    Gdiplus::GetImageEncoders (num, size, codecs);
    for (UINT i = 0; i < num; ++i)
        if (wcscmp (codecs[i].MimeType, format) == 0)
        {
            *clsid = codecs[i].Clsid;
            return (int) i;
        }
    return -1;
}

void shot (const std::string& file)
{
    RECT r;
    GetClientRect (window, &r);
    // 屏幕外的子窗口（原生输入框）不会自己重画，截图前强制画一遍。
    RedrawWindow (window, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    HDC screen = GetDC (nullptr);
    HDC dc = CreateCompatibleDC (screen);
    HBITMAP bitmap = CreateCompatibleBitmap (screen, r.right, r.bottom);
    auto* old = SelectObject (dc, bitmap);
    PrintWindow (window, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
    SelectObject (dc, old);
    {
        Gdiplus::Bitmap image (bitmap, nullptr);
        CLSID png;
        encoderClsid (L"image/png", &png);
        image.Save (widen (file).c_str(), &png, nullptr);
    }
    DeleteObject (bitmap);
    DeleteDC (dc);
    ReleaseDC (nullptr, screen);
    std::cout << "shot " << file << " " << r.right << "x" << r.bottom << std::endl;
}

struct Frame : public IPlugFrame
{
    tresult PLUGIN_API resizeView (IPlugView* view, ViewRect* rect) override
    {
        RECT r { 0, 0, rect->getWidth(), rect->getHeight() };
        SetWindowPos (window, nullptr, 0, 0, r.right, r.bottom, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        view->onSize (rect);
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface (const TUID, void**) override { return kNoInterface; }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};
} // namespace

int wmain (int argc, wchar_t** argv)
{
    if (argc != 3)
    {
        std::cout << "usage: LookupHost <plugin.vst3> <script>" << std::endl;
        return 3;
    }
    SetConsoleOutputCP (CP_UTF8);
    Gdiplus::GdiplusStartupInput gdiplusInput;
    ULONG_PTR gdiplusToken = 0;
    Gdiplus::GdiplusStartup (&gdiplusToken, &gdiplusInput, nullptr);
    OleInitialize (nullptr);

    std::string error;
    char path[MAX_PATH * 3];
    WideCharToMultiByte (CP_UTF8, 0, argv[1], -1, path, sizeof path, nullptr, nullptr);
    auto module = VST3::Hosting::Module::create (path, error);
    if (! module)
    {
        std::cout << "!! load failed: " << error << std::endl;
        return 5;
    }
    const auto& factory = module->getFactory();
    VST3::UID classId;
    for (const auto& info : factory.classInfos())
        if (info.category() == "Audio Module Class")
            classId = info.ID();
    std::cout << "class " << classId.toString() << std::endl;

    auto host = owned (new Vst::HostApplication());
    auto component = factory.createInstance<Vst::IComponent> (classId);
    if (! component || component->initialize (host) != kResultOk)
    {
        std::cout << "!! component failed" << std::endl;
        return 6;
    }
    FUnknownPtr<Vst::IEditController> controller (component);
    if (! controller)
    {
        std::cout << "!! no controller" << std::endl;
        return 6;
    }

    WNDCLASSW wc {};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW (nullptr);
    wc.lpszClassName = L"LookupHostWindow";
    RegisterClassW (&wc);
    // 屏幕外、不激活、不进任务栏。
    window = CreateWindowExW (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, L"LookupHost", WS_POPUP,
                              -20000, -20000, 860, 580, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow (window, SW_SHOWNOACTIVATE);

    Frame frame;
    IPtr<IPlugView> view;
    const auto open = [&] {
        view = owned (controller->createView (Vst::ViewType::kEditor));
        if (! view)
        {
            std::cout << "!! no view" << std::endl;
            return false;
        }
        view->setFrame (&frame);
        ViewRect size;
        view->getSize (&size);
        SetWindowPos (window, nullptr, 0, 0, size.getWidth(), size.getHeight(), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        const auto ok = view->attached (window, kPlatformTypeHWND) == kResultOk;
        std::cout << "view " << size.getWidth() << "x" << size.getHeight() << " resizable=" << (view->canResize() == kResultTrue) << std::endl;
        return ok;
    };
    const auto close = [&] {
        if (view)
        {
            view->removed();
            view->setFrame (nullptr);
            view = nullptr;
        }
    };
    if (! open())
        return 7;

    std::ifstream script (argv[2]);
    std::string line;
    while (std::getline (script, line))
    {
        if (! line.empty() && line.back() == '\r')
            line.pop_back();
        std::istringstream in (line);
        std::string command;
        in >> command;
        if (command.empty() || command[0] == '#')
            continue;
        if (command == "wait") { int ms = 0; in >> ms; pump ((DWORD) ms); }
        else if (command == "click" || command == "dblclick") { int x = 0, y = 0; in >> x >> y; click (x, y, command == "dblclick"); pump (100); }
        else if (command == "type") { std::string rest; std::getline (in >> std::ws, rest); type (rest); pump (100); }
        else if (command == "key") { std::string name; in >> name; key (name); pump (100); }
        else if (command == "shot") { std::string file; std::getline (in >> std::ws, file); pump (300); shot (file); }
        else if (command == "resize")
        {
            int w = 0, h = 0;
            in >> w >> h;
            ViewRect rect (0, 0, w, h);
            const auto check = view->checkSizeConstraint (&rect);
            std::cout << "resize " << w << "x" << h << " -> " << rect.getWidth() << "x" << rect.getHeight() << " check=" << check << std::endl;
            SetWindowPos (window, nullptr, 0, 0, rect.getWidth(), rect.getHeight(), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            view->onSize (&rect);
            pump (200);
        }
        else if (command == "reopen") { close(); pump (200); open(); }
        else if (command == "log") { std::string rest; std::getline (in >> std::ws, rest); std::cout << rest << std::endl; }
        else std::cout << "!! unknown command " << command << std::endl;
    }

    close();
    controller = nullptr;
    component->terminate();
    component = nullptr;
    DestroyWindow (window);
    module = nullptr; // 卸载插件 dll：后台线程没停的话这里会崩
    std::cout << "unloaded ok" << std::endl;
    OleUninitialize();
    Gdiplus::GdiplusShutdown (gdiplusToken);
    return 0;
}
