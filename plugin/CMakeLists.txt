cmake_minimum_required(VERSION 3.22)
project(WhatPluginLookup VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# 静态链接运行库，用户电脑上不需要另装 VC++ 运行库。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(JUCE_DIR "F:/DevTools/JUCE" CACHE PATH "JUCE source directory")
add_subdirectory(${JUCE_DIR} ${CMAKE_BINARY_DIR}/juce EXCLUDE_FROM_ALL)

# Standalone 只用来开发时测试界面，发布的是 VST3。
juce_add_plugin(WhatPluginLookup
    PRODUCT_NAME "WhatPluginLookup"
    COMPANY_NAME "WhatPlugin"
    BUNDLE_ID com.whatplugin.lookup
    PLUGIN_MANUFACTURER_CODE PVlt
    PLUGIN_CODE PVlk
    FORMATS VST3 Standalone
    IS_SYNTH FALSE
    NEEDS_MIDI_INPUT FALSE
    NEEDS_MIDI_OUTPUT FALSE
    VST3_CATEGORIES Fx Tools
    COPY_PLUGIN_AFTER_BUILD FALSE)

juce_generate_juce_header(WhatPluginLookup)
target_sources(WhatPluginLookup PRIVATE Main.cpp)

target_compile_definitions(WhatPluginLookup PUBLIC
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_DISPLAY_SPLASH_SCREEN=0
    JUCE_VST3_CAN_REPLACE_VST2=0)

if(MSVC)
    target_compile_options(WhatPluginLookup PRIVATE /utf-8)
endif()

target_link_libraries(WhatPluginLookup PRIVATE
    juce::juce_audio_utils
    juce::juce_recommended_config_flags
    juce::juce_recommended_warning_flags)
