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

option(MEGAPDF_CORE_TESTS "Build and register the core test target (#104)" OFF)
option(MEGAPDF_ASAN "AddressSanitizer for the core and its tests (GCC/Clang only)" OFF)

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
    set(PDFIUM_LINK ${PDFIUM_LIB})
    set(PDFIUM_RUNTIME ${PDFIUM_DLL})
elseif(APPLE)
    # tools/fetch-pdfium-mac.sh puts the universal prebuilt here (gitignored).
    set(PDFIUM_MAC ${REPO}/libs/pdfium/mac-univ)
    if(NOT EXISTS ${PDFIUM_MAC}/lib/libpdfium.dylib)
        message(FATAL_ERROR "libs/pdfium/mac-univ is missing; run tools/fetch-pdfium-mac.sh first")
    endif()
    set(PDFIUM_LINK ${PDFIUM_MAC}/lib/libpdfium.dylib)
    set(PDFIUM_RUNTIME ${PDFIUM_LINK})
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
    # MEGAPDF_PDFIUM_DIR points at another build's staging directory, such as a local
    # tools/pdfium/build-pdfium.sh run, to test a PDFium patch before it is released (#120).
    set(MEGAPDF_PDFIUM_DIR "${REPO}/libs/pdfium/linux-x64" CACHE PATH "PDFium to link on Linux (include/, lib/, VERSION)")
    set(PDFIUM_LINUX ${MEGAPDF_PDFIUM_DIR})
    if(NOT EXISTS ${PDFIUM_LINUX}/lib/libpdfium.so)
        message(FATAL_ERROR "libs/pdfium/linux-x64 is missing (fetch the pinned pdfium-binaries linux-x64 build)")
    endif()
    set(PDFIUM_LINK ${PDFIUM_LINUX}/lib/libpdfium.so)
    set(PDFIUM_RUNTIME ${PDFIUM_LINK})
    set_target_properties(megapdf_core PROPERTIES BUILD_RPATH "$ORIGIN" INSTALL_RPATH "$ORIGIN")
endif()

target_link_libraries(megapdf_core PRIVATE ${PDFIUM_LINK})

# How many MegaPDF patches the linked PDFium carries (tools/pdfium/patches, #119). The
# core tests expect a page rewrite to keep what each patch fixes, and nothing more.
get_filename_component(PDFIUM_LIB_DIR ${PDFIUM_RUNTIME} DIRECTORY)
if(WIN32)
    # pdfium.dll sits beside VERSION; the import library is generated in the build tree.
    set(PDFIUM_VERSION_FILE ${PDFIUM_LIB_DIR}/VERSION)
else()
    set(PDFIUM_VERSION_FILE ${PDFIUM_LIB_DIR}/../VERSION)
endif()
set(MEGAPDF_PDFIUM_PATCHES 0)
if(EXISTS ${PDFIUM_VERSION_FILE})
    file(STRINGS ${PDFIUM_VERSION_FILE} patch_line REGEX "^MEGAPDF_PATCHES=")
    if(patch_line)
        string(REGEX REPLACE "^MEGAPDF_PATCHES=" "" MEGAPDF_PDFIUM_PATCHES "${patch_line}")
    endif()
endif()
message(STATUS "pdfium: ${PDFIUM_LINK} (MegaPDF patches: ${MEGAPDF_PDFIUM_PATCHES})")

if(MEGAPDF_ASAN AND NOT MSVC)
    target_compile_options(megapdf_core PRIVATE -fsanitize=address -fno-omit-frame-pointer -g)
    target_link_options(megapdf_core PRIVATE -fsanitize=address)
endif()

# ---------------------------------------------------------------------------
# Test target (#104): the assertions the platform suites make, run against the
# implementation itself on every CI OS. Needs the generated fixtures and the
# #98 schematic; see .github/workflows/core-tests.yml for the invocation.
# ---------------------------------------------------------------------------
if(MEGAPDF_CORE_TESTS)
    enable_testing()
    set(MEGAPDF_FIXTURES_DIR "" CACHE PATH "Output directory of tools/gen_test_fixtures.py")
    set(MEGAPDF_SCHEMATIC "${REPO}/tests/MegaPDF.Core.Tests/Fixtures/microbit-v2-schematic.pdf"
        CACHE FILEPATH "The #98 search canary")
    if(MEGAPDF_FIXTURES_DIR STREQUAL "")
        message(FATAL_ERROR "MEGAPDF_CORE_TESTS needs -DMEGAPDF_FIXTURES_DIR=<dir written by tools/gen_test_fixtures.py>")
    endif()

    set(MEGAPDF_TEXT_EXPECTED "${CMAKE_CURRENT_SOURCE_DIR}/tests/expected/text_runs.txt" CACHE FILEPATH
        "The desktop engine's text runs and lines for every fixture, captured before #106 moved them into the core")
    add_executable(megapdf_core_tests tests/core_tests.cpp)
    target_include_directories(megapdf_core_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${REPO}/libs/pdfium/android/include)
    target_compile_definitions(megapdf_core_tests PRIVATE MEGAPDF_PDFIUM_PATCHES=${MEGAPDF_PDFIUM_PATCHES}
        # Committed encrypted fixtures (#131, tools/gen_security_fixtures.sh).
        "MEGAPDF_SECURITY_FIXTURES=\"${REPO}/tests/MegaPDF.Core.Tests/Fixtures/security\""
        # Committed real-font fixtures, e.g. the embedded subset font of #130.
        "MEGAPDF_REPO_FIXTURES=\"${REPO}/tests/MegaPDF.Core.Tests/Fixtures\"")
    target_link_libraries(megapdf_core_tests PRIVATE megapdf_core ${PDFIUM_LINK})
    if(MEGAPDF_ASAN AND NOT MSVC)
        target_compile_options(megapdf_core_tests PRIVATE -fsanitize=address -fno-omit-frame-pointer -g)
        target_link_options(megapdf_core_tests PRIVATE -fsanitize=address)
    endif()

    # The #173 leak checker: the same searches the core tests run, as a tool the corpus
    # battery drives (tools/leakcheck). Built with the tests so a change to one is a change
    # to both.
    add_executable(megapdf_leakcheck ${REPO}/tools/leakcheck/leakcheck.cpp)
    target_include_directories(megapdf_leakcheck PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${REPO}/tools/leakcheck
        ${REPO}/libs/pdfium/android/include)
    target_link_libraries(megapdf_leakcheck PRIVATE megapdf_core ${PDFIUM_LINK})
    add_custom_command(TARGET megapdf_leakcheck POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different ${PDFIUM_RUNTIME} $<TARGET_FILE_DIR:megapdf_leakcheck>)
    if(APPLE)
        set_target_properties(megapdf_leakcheck PROPERTIES BUILD_RPATH "@loader_path")
        add_custom_command(TARGET megapdf_leakcheck POST_BUILD
            COMMAND install_name_tool -change ./libpdfium.dylib @loader_path/libpdfium.dylib $<TARGET_FILE:megapdf_leakcheck>)
    elseif(UNIX)
        set_target_properties(megapdf_leakcheck PROPERTIES BUILD_RPATH "$ORIGIN")
    endif()

    # pdfium beside the test binary, and the binary looking beside itself.
    add_custom_command(TARGET megapdf_core_tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different ${PDFIUM_RUNTIME} $<TARGET_FILE_DIR:megapdf_core_tests>)
    if(APPLE)
        set_target_properties(megapdf_core_tests PROPERTIES BUILD_RPATH "@loader_path")
        add_custom_command(TARGET megapdf_core_tests POST_BUILD
            COMMAND install_name_tool -change ./libpdfium.dylib @loader_path/libpdfium.dylib $<TARGET_FILE:megapdf_core_tests>)
    elseif(UNIX)
        set_target_properties(megapdf_core_tests PROPERTIES BUILD_RPATH "$ORIGIN")
    endif()

    add_test(NAME core
        COMMAND megapdf_core_tests ${MEGAPDF_FIXTURES_DIR} ${MEGAPDF_SCHEMATIC} ${MEGAPDF_TEXT_EXPECTED}
        WORKING_DIRECTORY $<TARGET_FILE_DIR:megapdf_core_tests>)
endif()
