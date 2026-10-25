cmake_minimum_required(VERSION 3.21)
project(UnityCefPlugin LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Target Windows 10 to expose newer API definitions in Win32 headers under MinGW
add_compile_definitions(_WIN32_WINNT=0x0A00 WINVER=0x0A00)

set(CEF_ROOT "" CACHE PATH "Path to the CEF binary distribution root")
if(NOT CEF_ROOT AND DEFINED ENV{CEF_ROOT})
  set(CEF_ROOT "$ENV{CEF_ROOT}")
endif()
if(NOT CEF_ROOT)
  message(FATAL_ERROR "CEF_ROOT is required. Pass -DCEF_ROOT=/path/to/cef_binary or set the CEF_ROOT environment variable.")
endif()

list(APPEND CMAKE_MODULE_PATH "${CEF_ROOT}/cmake")

find_package(CEF REQUIRED)

# Overwrite CEF's MSVC-specific compiler flags with GCC/MinGW compatible ones
if (CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
  set(CEF_COMPILER_FLAGS -Wall -Wno-unused-parameter -Wno-missing-field-initializers -Wno-attributes -Wno-ignored-attributes -Wno-pragma-pack)
  set(CEF_COMPILER_FLAGS_DEBUG -g -O0)
  set(CEF_COMPILER_FLAGS_RELEASE -O2 -DNDEBUG)
  # Newer GCC no longer pulls <cstdint> in transitively; CEF's wrapper sources assume it does.
  set(CEF_CXX_COMPILER_FLAGS -std=c++20 -fno-rtti -fno-exceptions -include cstdint)
  set(CEF_C_COMPILER_FLAGS -std=c99)
  set(CEF_LINKER_FLAGS "")
  set(CEF_LINKER_FLAGS_DEBUG "")
  set(CEF_LINKER_FLAGS_RELEASE "")
  set(CEF_DELAYLOAD_FLAGS "")

  # Statically link C/C++ runtimes and thread libraries so the plugin doesn't depend on external MinGW DLLs
  set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -static-libgcc -static-libstdc++ -static")
  set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -static-libgcc -static-libstdc++ -static")
endif()

# We need to compile libcef_dll_wrapper from the CEF binary distribution
add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)

if(APPLE)
  # macOS (build-cef-mac.sh): a loadable bundle and the helper binary, both loading the framework at runtime;
  # the Unity build assembles the helper apps and the player around them (Assets/Editor/CefPlayerPayload.cs).
  set(UNITY_CEF_MAC_FLAGS -fno-rtti -fno-exceptions -fvisibility=hidden -fvisibility-inlines-hidden)
  add_library(UnityCefPlugin MODULE UnityCefPlugin.cpp UnityCefMac.mm)
  set_target_properties(UnityCefPlugin PROPERTIES BUNDLE TRUE BUNDLE_EXTENSION bundle)
  target_compile_options(UnityCefPlugin PRIVATE ${UNITY_CEF_MAC_FLAGS})
  target_include_directories(UnityCefPlugin PRIVATE ${CEF_INCLUDE_PATH})
  target_link_libraries(UnityCefPlugin PRIVATE libcef_dll_wrapper ${CEF_STANDARD_LIBS})
  add_executable(UnityCefHelper UnityCefHelper.cpp)
  target_compile_options(UnityCefHelper PRIVATE ${UNITY_CEF_MAC_FLAGS})
  target_include_directories(UnityCefHelper PRIVATE ${CEF_INCLUDE_PATH})
  target_link_libraries(UnityCefHelper PRIVATE libcef_dll_wrapper ${CEF_STANDARD_LIBS})
  return()
endif()

# Add our plugin DLL
add_library(UnityCefPlugin SHARED UnityCefPlugin.cpp)
target_include_directories(UnityCefPlugin PRIVATE ${CEF_INCLUDE_PATH})
target_link_libraries(UnityCefPlugin PRIVATE libcef_dll_wrapper ${CEF_LIB_RELEASE})

# Add our helper executable
add_executable(UnityCefHelper UnityCefHelper.cpp)
target_include_directories(UnityCefHelper PRIVATE ${CEF_INCLUDE_PATH})
target_link_libraries(UnityCefHelper PRIVATE libcef_dll_wrapper ${CEF_LIB_RELEASE})

set(UNITY_PLUGIN_OUTPUT_DIR "${CMAKE_CURRENT_LIST_DIR}/../Assets/Plugins/x86_64" CACHE PATH "Unity x86_64 plugin output directory")
# No "lib" prefix under MinGW: Unity's DllImport names it UnityCefPlugin.dll.
set_target_properties(UnityCefPlugin PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${UNITY_PLUGIN_OUTPUT_DIR}" PREFIX "")
set_target_properties(UnityCefHelper PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${UNITY_PLUGIN_OUTPUT_DIR}")
