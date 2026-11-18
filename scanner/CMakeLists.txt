cmake_minimum_required(VERSION 3.22)
project(WhatPluginScanner VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# 静态链接 C++ 运行库，扫描器不依赖额外的运行库安装。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# 只用 VST3 SDK 的接口头文件（MIT 许可）读工厂清单，不用任何插件框架。
# 指向 VST3 SDK 的根目录（里面有 pluginterfaces）。
set(VST3_SDK "F:/DevTools/vst3sdk" CACHE PATH "VST3 SDK root (contains pluginterfaces)")
if(NOT EXISTS "${VST3_SDK}/pluginterfaces/base/ipluginbase.h")
    message(FATAL_ERROR "VST3_SDK 不对：${VST3_SDK} 里没有 pluginterfaces")
endif()

add_executable(WhatPluginScanner
    Main.cpp
    "${VST3_SDK}/pluginterfaces/base/funknown.cpp")
target_include_directories(WhatPluginScanner PRIVATE "${VST3_SDK}")
# _CRT_SECURE_NO_WARNINGS：SDK 的 funknown.cpp 用了 sprintf / strcpy。
target_compile_definitions(WhatPluginScanner PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
# 源码是 UTF-8；不指定时中文系统的 MSVC 会按 GBK 读取，把注释后的代码吞掉。
target_compile_options(WhatPluginScanner PRIVATE /utf-8 /W4)
target_link_libraries(WhatPluginScanner PRIVATE ole32)
# 输出位置和以前一样，主程序和命令行工具的项目文件从这里取。
set_target_properties(WhatPluginScanner PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/WhatPluginScanner_artefacts/$<CONFIG>")
