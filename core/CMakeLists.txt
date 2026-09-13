# The shared engine core as a standalone native library, for the platforms that
# consume it through P/Invoke: Windows (megapdf_core.dll) and macOS
# (libmegapdf_core.dylib), both behind MegaPDF.Core's IPdfEngine adapter (#38,
# ADR-003). Android and iOS do not use this file — they compile the same sources
# straight into their own binaries (see android/engine/src/main/cpp/CMakeLists.txt
# and ios/project.yml).
#
# Invoked by tools/build-core.ps1 (Windows) and tools/build-core.sh (macOS), which
# MegaPDF.Core.csproj runs when the artifact is missing or older than the sources.
cmake_minimum_required(VERSION 3.22)
# Must precede project(): the architectures are read when the toolchain is set up.
# Universal, like the pdfium prebuilt it links, so an x86_64 Mac build is not
# silently arm64-only.
if(CMAKE_HOST_APPLE)
    set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "" FORCE)
endif()
project(megapdf_core CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(REPO ${CMAKE_CURRENT_SOURCE_DIR}/..)

add_library(megapdf_core SHARED megapdf_core.cpp megapdf_core.h)
# One header set for every platform: the prebuilts are all the same pdfium-binaries
# release (libs/pdfium/*/VERSION), and only the Android tree vendors the headers.
target_include_directories(megapdf_core PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${REPO}/libs/pdfium/android/include)

if(WIN32)
    # libs/pdfium/win-x64 vendors only pdfium.dll: no import library. It is
    # generated here from the DLL's export table with the MSVC tools that live
    # next to the linker CMake found.
    set(PDFIUM_DLL ${REPO}/libs/pdfium/win-x64/pdfium.dll)
    get_filename_component(MSVC_BIN "${CMAKE_LINKER}" DIRECTORY)
    find_program(DUMPBIN_EXE dumpbin HINTS ${MSVC_BIN} REQUIRED)
    find_program(LIB_EXE lib HINTS ${MSVC_BIN} REQUIRED)

    set(PDFIUM_DEF ${CMAKE_BINARY_DIR}/pdfium.def)
    set(PDFIUM_LIB ${CMAKE_BINARY_DIR}/pdfium.lib)
    if(NOT EXISTS ${PDFIUM_LIB} OR ${PDFIUM_DLL} IS_NEWER_THAN ${PDFIUM_LIB})
        execute_process(
            COMMAND ${DUMPBIN_EXE} /NOLOGO /EXPORTS ${PDFIUM_DLL}
            OUTPUT_VARIABLE exports RESULT_VARIABLE rc)
        if(NOT rc EQUAL 0)
            message(FATAL_ERROR "dumpbin failed on ${PDFIUM_DLL}")
        endif()
        # Export-table rows look like: "   ordinal  hint  RVA  name"
        string(REGEX MATCHALL "\n +[0-9]+ +[0-9A-Fa-f]+ +[0-9A-Fa-f]+ +[A-Za-z_][A-Za-z0-9_]*" rows "${exports}")
        set(names "")
        foreach(row IN LISTS rows)
            string(REGEX REPLACE ".* ([A-Za-z_][A-Za-z0-9_]*)$" "\\1" name "${row}")
            list(APPEND names ${name})
        endforeach()
        list(LENGTH names count)
        if(count LESS 100)
            message(FATAL_ERROR "only ${count} exports parsed from pdfium.dll; the dumpbin format may have changed")
        endif()
        string(REPLACE ";" "\n  " body "${names}")
        file(WRITE ${PDFIUM_DEF} "LIBRARY pdfium\nEXPORTS\n  ${body}\n")
        execute_process(
            COMMAND ${LIB_EXE} /NOLOGO /DEF:${PDFIUM_DEF} /MACHINE:X64 /OUT:${PDFIUM_LIB}
            RESULT_VARIABLE rc OUTPUT_QUIET)
        if(NOT rc EQUAL 0)
            message(FATAL_ERROR "lib.exe could not build the pdfium import library")
        endif()
        message(STATUS "pdfium import library: ${count} exports")
    endif()

    target_compile_definitions(megapdf_core PRIVATE MEGAPDF_CORE_BUILD)
    target_link_libraries(megapdf_core PRIVATE ${PDFIUM_LIB})
elseif(APPLE)
    # tools/fetch-pdfium-mac.sh puts the universal prebuilt here (gitignored).
    set(PDFIUM_MAC ${REPO}/libs/pdfium/mac-univ)
    if(NOT EXISTS ${PDFIUM_MAC}/lib/libpdfium.dylib)
        message(FATAL_ERROR "libs/pdfium/mac-univ is missing; run tools/fetch-pdfium-mac.sh first")
    endif()
    target_link_libraries(megapdf_core PRIVATE ${PDFIUM_MAC}/lib/libpdfium.dylib)
    # Both dylibs ship side by side in Contents/MacOS. The prebuilt's install name
    # is "./libpdfium.dylib", which dyld would resolve against the working
    # directory; rewrite our reference to load it from beside us instead.
    set_target_properties(megapdf_core PROPERTIES
        INSTALL_RPATH "@loader_path"
        BUILD_WITH_INSTALL_RPATH TRUE
        MACOSX_RPATH TRUE)
    add_custom_command(TARGET megapdf_core POST_BUILD
        COMMAND install_name_tool -change ./libpdfium.dylib @loader_path/libpdfium.dylib $<TARGET_FILE:megapdf_core>
        COMMENT "pointing libmegapdf_core at the libpdfium.dylib beside it")
else()
    # Linux is the core test target's home (#104); no app ships from here.
    set(PDFIUM_LINUX ${REPO}/libs/pdfium/linux-x64)
    if(NOT EXISTS ${PDFIUM_LINUX}/lib/libpdfium.so)
        message(FATAL_ERROR "libs/pdfium/linux-x64 is missing (fetch the pinned pdfium-binaries linux-x64 build)")
    endif()
    target_link_libraries(megapdf_core PRIVATE ${PDFIUM_LINUX}/lib/libpdfium.so)
endif()
