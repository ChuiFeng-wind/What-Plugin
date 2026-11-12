cmake_minimum_required(VERSION 3.22)
project(WhatPluginScanner VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# 静态链接 C++ 运行库，扫描器不依赖额外的运行库安装。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(JUCE_DIR "F:/DevTools/JUCE" CACHE PATH "JUCE source directory")
add_subdirectory(${JUCE_DIR} ${CMAKE_BINARY_DIR}/juce EXCLUDE_FROM_ALL)

# 只用 VST3 接口头文件读工厂清单。不用 JUCE 的 VST3 宿主：它列举时会创建每个插件实例。
set(VST3_SDK "${JUCE_DIR}/modules/juce_audio_processors_headless/format_types/VST3_SDK")

juce_add_console_app(WhatPluginScanner PRODUCT_NAME "WhatPluginScanner")
target_sources(WhatPluginScanner PRIVATE
    Main.cpp
    "${VST3_SDK}/pluginterfaces/base/funknown.cpp")
target_include_directories(WhatPluginScanner PRIVATE "${VST3_SDK}")
# 源码是 UTF-8；不指定时中文系统的 MSVC 会按 GBK 读取，把注释后的代码吞掉。
target_compile_options(WhatPluginScanner PRIVATE /utf-8)

target_compile_definitions(WhatPluginScanner PRIVATE
    JUCE_USE_CURL=0
    JUCE_WEB_BROWSER=0
    JUCE_DISPLAY_SPLASH_SCREEN=0
    JUCE_STANDALONE_APPLICATION=1)

target_link_libraries(WhatPluginScanner PRIVATE
    juce::juce_core
    juce::juce_recommended_config_flags)
