cmake_minimum_required(VERSION 3.25)

# 只用 VST3 SDK（MIT）和它带的 VSTGUI（BSD），不用其他插件框架。
# Mac：Apple 芯片和 Intel 合成一个通用插件，macOS 11 起可用（和扫描器一样）。project() 之前 APPLE 还没设，用 CMAKE_HOST_APPLE。
if(CMAKE_HOST_APPLE)
    if(NOT CMAKE_OSX_ARCHITECTURES)
        set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "")
    endif()
    if(NOT CMAKE_OSX_DEPLOYMENT_TARGET)
        set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "")
    endif()
endif()

# 指向 VST3 SDK 的根目录（git clone --recurse-submodules https://github.com/steinbergmedia/vst3sdk）。
set(vst3sdk_SOURCE_DIR "F:/DevTools/vst3sdk" CACHE PATH "VST3 SDK root")
if(NOT EXISTS "${vst3sdk_SOURCE_DIR}/vstgui4/vstgui/vstgui.h")
    message(FATAL_ERROR "vst3sdk_SOURCE_DIR 不对：${vst3sdk_SOURCE_DIR} 里没有 VST3 SDK 和 VSTGUI")
endif()

# 不编译 SDK 的示例；不在系统 VST3 文件夹里建链接（要管理员权限，也会碰到正在用的插件目录）。
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT ON CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
# 静态链接运行库，用户电脑上不需要另装 VC++ 运行库。
set(SMTG_USE_STATIC_CRT ON CACHE BOOL "" FORCE)
# 插件文件夹的图标用 WhatPlugin 自己的；不指定时 SDK 会放 Steinberg 的 VST 标志。
set(SMTG_PACKAGE_ICON_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../src/WhatPlugin.App/app.ico")
# 编译后用 SDK 的验证工具检查插件。
set(SMTG_RUN_VST_VALIDATOR ON CACHE BOOL "" FORCE)

project(WhatPluginLookup VERSION 1.0.0 LANGUAGES C CXX)

add_subdirectory(${vst3sdk_SOURCE_DIR} ${PROJECT_BINARY_DIR}/vst3sdk)
smtg_enable_vst3_sdk()

smtg_add_vst3plugin(WhatPluginLookup
    source/Main.cpp
    source/LookupView.cpp
    source/LookupView.h
    source/Runner.cpp
    source/Runner.h
    source/Json.cpp
    source/Json.h
    # 处理器和控制器合在一个类里，SDK 的 sdk 库不带它，要单独编译。
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/vstsinglecomponenteffect.cpp
    resource/lookup.uidesc)

target_compile_features(WhatPluginLookup PUBLIC cxx_std_17)
target_link_libraries(WhatPluginLookup PRIVATE sdk vstgui_support)
smtg_target_add_plugin_resources(WhatPluginLookup RESOURCES resource/lookup.uidesc)

if(SMTG_MAC)
    # 插件包的 Info.plist 用 SDK 的模板；剪贴板用 AppKit 的 NSPasteboard。
    smtg_target_set_bundle(WhatPluginLookup
        BUNDLE_IDENTIFIER "app.whatplugin.lookup"
        COMPANY_NAME "WhatPlugin")
    target_link_libraries(WhatPluginLookup PRIVATE "-framework AppKit")
endif()

if(MSVC)
    # 源码是 UTF-8；不指定时中文系统的 MSVC 会按 GBK 读取。
    target_compile_options(WhatPluginLookup PRIVATE /utf-8)
endif()

# 开发时用的测试宿主：把界面开在屏幕外，按脚本操作并截图。不进安装包。
option(WHATPLUGIN_BUILD_TEST_HOST "Build the off-screen test host" OFF)
if(WHATPLUGIN_BUILD_TEST_HOST)
    add_executable(LookupHost test/LookupHost.cpp
        ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp)
    target_link_libraries(LookupHost PRIVATE sdk_hosting)
    target_compile_options(LookupHost PRIVATE /utf-8)
    target_compile_definitions(LookupHost PRIVATE UNICODE _UNICODE)
endif()

# Runner 的测试：不开 DAW，直接运行命令行工具检查输出。Windows 和 Mac 都能编，CI 在 Mac 上跑。
option(WHATPLUGIN_BUILD_RUNNER_TEST "Build the Runner test" OFF)
if(WHATPLUGIN_BUILD_RUNNER_TEST)
    add_executable(RunnerTest test/RunnerTest.cpp source/Runner.cpp)
    target_compile_features(RunnerTest PRIVATE cxx_std_17)
    if(MSVC)
        target_compile_options(RunnerTest PRIVATE /utf-8)
    endif()
    if(APPLE)
        target_link_libraries(RunnerTest PRIVATE "-framework AppKit")
    endif()
endif()
