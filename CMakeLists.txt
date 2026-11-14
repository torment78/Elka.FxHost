cmake_minimum_required(VERSION 3.24)

project(ElkaFxHost
    VERSION 0.7.6.3
    DESCRIPTION "Elka FX Host: VST host behind its own virtual ASIO driver"
    LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(ELKA_JUCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/JUCE")
set(ELKA_VST2_SDK_PATH "" CACHE PATH "Required legacy VST2 SDK folder containing pluginterfaces/vst2.x/aeffect.h and aeffectx.h")
set(ELKA_ENABLE_VST2_HOST OFF)
if (EXISTS "${ELKA_JUCE_DIR}/CMakeLists.txt")
    if (NOT EXISTS "${ELKA_JUCE_DIR}/extras/Build/CMake/JUCEModuleSupport.cmake")
        message(FATAL_ERROR
            "The JUCE checkout in external/JUCE is incomplete. Missing extras/Build/CMake/JUCEModuleSupport.cmake. "
            "If this was cloned from Git, make sure external/JUCE/extras/Build is committed/pulled, or replace external/JUCE with a full JUCE checkout.")
    endif()

    set(JUCE_MODULES_ONLY ON CACHE BOOL "Build JUCE modules only" FORCE)
    add_subdirectory("${ELKA_JUCE_DIR}" external/JUCE-build EXCLUDE_FROM_ALL)
    set(ELKA_ENABLE_JUCE_PLUGIN_HOST ON)

    if (NOT ELKA_VST2_SDK_PATH)
        message(FATAL_ERROR "VST2 hosting is required. Set ELKA_VST2_SDK_PATH to a legacy VST2 SDK folder, for example external/VST2_SDK, containing pluginterfaces/vst2.x/aeffect.h and pluginterfaces/vst2.x/aeffectx.h.")
    endif()

    if (NOT EXISTS "${ELKA_VST2_SDK_PATH}/pluginterfaces/vst2.x/aeffect.h"
        OR NOT EXISTS "${ELKA_VST2_SDK_PATH}/pluginterfaces/vst2.x/aeffectx.h")
        message(FATAL_ERROR "VST2 hosting is required, but ELKA_VST2_SDK_PATH='${ELKA_VST2_SDK_PATH}' does not contain pluginterfaces/vst2.x/aeffect.h and aeffectx.h.")
    endif()

    set(ELKA_ENABLE_VST2_HOST ON)
    if (COMMAND juce_set_vst2_sdk_path)
        juce_set_vst2_sdk_path("${ELKA_VST2_SDK_PATH}")
    endif()
else()
    message(FATAL_ERROR "JUCE is required for VST2/VST3 hosting. Put JUCE in external/JUCE before configuring this project.")
endif()

set(ELKA_REALTIME_CALLBACK_SOURCES
    src/engine/RealtimeEngine.cpp
    src/engine/AudioThreadPriority.cpp
)

set(ELKA_NATIVE_CORE_SOURCES
    ${ELKA_REALTIME_CALLBACK_SOURCES}
    src/matrix_link/MatrixLinkHost.cpp
    src/plugins/PluginHostLayer.cpp
)

add_library(ElkaFxHost.Native SHARED
    src/native_api/FxHostNativeApi.cpp
    ${ELKA_NATIVE_CORE_SOURCES}
)

# The helper hosts the existing native plugin layer directly, with no CLR or P/Invoke.
add_executable(Elka.PluginWorker WIN32
    src/plugin-worker/NativeWorker.cpp
    src/plugins/PluginHostLayer.cpp
)

foreach(target IN ITEMS ElkaFxHost.Native Elka.PluginWorker)
    target_include_directories(${target} PRIVATE
        src
    )

    target_compile_definitions(${target} PRIVATE
        UNICODE
        _UNICODE
        NOMINMAX
        WIN32_LEAN_AND_MEAN
        JUCE_WEB_BROWSER=0
        JUCE_USE_CURL=0
        JUCE_ASIO=1
        JUCE_ASIO_USE_EXTERNAL_SDK=0
        JUCE_DISABLE_ASSERTIONS=1
    )

    target_link_libraries(${target} PRIVATE
        Advapi32
        Avrt
        Comctl32
        Gdi32
        Mmdevapi
        Ole32
        Propsys
        Shell32
        User32
    )
endforeach()

target_compile_definitions(ElkaFxHost.Native PRIVATE
    ELKA_VOICEMEETER_FX_HOST_NATIVE_EXPORTS=1
)

if (ELKA_ENABLE_JUCE_PLUGIN_HOST)
    foreach(target IN ITEMS ElkaFxHost.Native Elka.PluginWorker)
        target_compile_definitions(${target} PRIVATE
            ELKA_ENABLE_JUCE_PLUGIN_HOST=1
            ELKA_ENABLE_VST2_HOST=$<BOOL:${ELKA_ENABLE_VST2_HOST}>
            JUCE_PLUGINHOST_VST3=1
            JUCE_PLUGINHOST_VST=$<BOOL:${ELKA_ENABLE_VST2_HOST}>
        )

        if (ELKA_ENABLE_VST2_HOST)
            target_include_directories(${target} PRIVATE
                "${ELKA_VST2_SDK_PATH}"
            )
        endif()

        target_link_libraries(${target} PRIVATE
            juce::juce_audio_devices
            juce::juce_audio_processors
            juce::juce_dsp
        )
    endforeach()
else()
    foreach(target IN ITEMS ElkaFxHost.Native)
        target_compile_definitions(${target} PRIVATE
            ELKA_ENABLE_JUCE_PLUGIN_HOST=0
        )
    endforeach()
endif()

if (MSVC)
    foreach(target IN ITEMS ElkaFxHost.Native Elka.PluginWorker)
        target_compile_options(${target} PRIVATE
            /W4
            /permissive-
            /EHsc
        )
    endforeach()
endif()


# Offline monitor checks are opt-in and never connect to VoiceMeeter.
add_executable(SignalMonitorChecks EXCLUDE_FROM_ALL tests/SignalMonitorChecks/main.cpp)
target_include_directories(SignalMonitorChecks PRIVATE src)
target_compile_definitions(SignalMonitorChecks PRIVATE JUCE_WEB_BROWSER=0 JUCE_USE_CURL=0 NOMINMAX)
target_link_libraries(SignalMonitorChecks PRIVATE juce::juce_dsp)

# Editor lifecycle checks use a synthetic plugin, with no audio device or remote API.
add_executable(PluginEditorChecks EXCLUDE_FROM_ALL tests/PluginEditorChecks/main.cpp)
target_include_directories(PluginEditorChecks PRIVATE src)
target_compile_definitions(PluginEditorChecks PRIVATE JUCE_WEB_BROWSER=0 JUCE_USE_CURL=0 NOMINMAX)
target_link_libraries(PluginEditorChecks PRIVATE juce::juce_audio_processors)

# Synthetic callback checks never attach to VoiceMeeter or open an audio device.
add_executable(DspPerformanceChecks EXCLUDE_FROM_ALL
    tests/DspPerformanceChecks/main.cpp src/engine/RealtimeEngine.cpp)
target_include_directories(DspPerformanceChecks PRIVATE src)
target_compile_definitions(DspPerformanceChecks PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(DspPerformanceChecks PRIVATE Avrt)

add_executable(PeakHoldChecks EXCLUDE_FROM_ALL tests/PeakHoldChecks/main.cpp src/engine/RealtimeEngine.cpp)
target_include_directories(PeakHoldChecks PRIVATE src)
target_compile_definitions(PeakHoldChecks PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(PeakHoldChecks PRIVATE Avrt)

add_executable(WidgetLevelChecks EXCLUDE_FROM_ALL tests/WidgetLevelChecks/main.cpp src/engine/RealtimeEngine.cpp)
target_include_directories(WidgetLevelChecks PRIVATE src)
target_compile_definitions(WidgetLevelChecks PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(WidgetLevelChecks PRIVATE Avrt)

add_executable(OutputBoostChecks EXCLUDE_FROM_ALL tests/OutputBoostChecks/main.cpp src/engine/RealtimeEngine.cpp)
target_include_directories(OutputBoostChecks PRIVATE src)
target_compile_definitions(OutputBoostChecks PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(OutputBoostChecks PRIVATE Avrt)

# Shared-container identity checks do not load plugins or connect to audio.
add_executable(PluginContainerChecks EXCLUDE_FROM_ALL tests/PluginContainerChecks/main.cpp)
target_include_directories(PluginContainerChecks PRIVATE src)

# Exercise the production priority helper against simulated OS calls, not audio threads.
add_executable(AudioThreadPriorityChecks EXCLUDE_FROM_ALL
    tests/AudioThreadPriorityChecks/main.cpp src/engine/AudioThreadPriority.cpp)
target_include_directories(AudioThreadPriorityChecks PRIVATE tests/AudioThreadPriorityChecks/fakes src)

include(tests/PluginFailureChecks/CMakeLists.txt)
include(tests/PluginCapacityChecks/CMakeLists.txt)
include(tests/SandboxPoolChecks/CMakeLists.txt)

add_library(NativeWorkerProbeVst MODULE EXCLUDE_FROM_ALL tests/NativeWorkerChecks/ProbeVst.cpp)
target_include_directories(NativeWorkerProbeVst PRIVATE "${ELKA_VST2_SDK_PATH}")
target_link_libraries(NativeWorkerProbeVst PRIVATE User32)

# ---------------------------------------------------------------------------------
# Elka FX Host virtual ASIO driver. Loaded by Matrix (or any ASIO host) in its own
# process; talks to the FX Host app through src/matrix_link/MatrixLinkProtocol.h.
# Static CRT so it never depends on the host process having our runtime.
add_library(ElkaFxHost.Asio SHARED
    src/asio_driver/ElkaAsioDriver.cpp
    src/asio_driver/ElkaFxHost.Asio.def
)
target_include_directories(ElkaFxHost.Asio PRIVATE src "${ELKA_JUCE_DIR}/modules")
target_compile_definitions(ElkaFxHost.Asio PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(ElkaFxHost.Asio PRIVATE Advapi32 Avrt Ole32 User32 Winmm)
set_property(TARGET ElkaFxHost.Asio PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if (MSVC)
    target_compile_options(ElkaFxHost.Asio PRIVATE /W4 /permissive- /EHsc)
endif()

# Stand-in for Matrix: loads the driver DLL directly and loops a tone through FX In/Out.
add_executable(MatrixLinkLoopback tools/MatrixLinkLoopback/main.cpp)
target_include_directories(MatrixLinkLoopback PRIVATE src "${ELKA_JUCE_DIR}/modules")
target_compile_definitions(MatrixLinkLoopback PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(MatrixLinkLoopback PRIVATE Ole32 User32)
add_dependencies(MatrixLinkLoopback ElkaFxHost.Asio)

# FX Host severs FX In from FX Out: only drawn lines pass audio.
add_executable(DryPathChecks EXCLUDE_FROM_ALL tests/DryPathChecks/main.cpp src/engine/RealtimeEngine.cpp)
target_include_directories(DryPathChecks PRIVATE src)
target_compile_definitions(DryPathChecks PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(DryPathChecks PRIVATE Avrt)

# Follows a real device's clock for ~15 s (silence only). Not part of the default build.
add_executable(ClockFollowerChecks EXCLUDE_FROM_ALL tests/ClockFollowerChecks/main.cpp)
target_include_directories(ClockFollowerChecks PRIVATE src)
target_compile_definitions(ClockFollowerChecks PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(ClockFollowerChecks PRIVATE Ole32)
