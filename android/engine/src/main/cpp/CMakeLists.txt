cmake_minimum_required(VERSION 3.22.1)
project(megapdf_engine CXX)

# Prebuilt PDFium (bblanchon/pdfium-binaries, pinned to the same major version
# as libs/pdfium/win-x64 — see libs/pdfium/android/VERSION).
set(PDFIUM_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../../../../libs/pdfium/android)

add_library(pdfium SHARED IMPORTED)
set_target_properties(pdfium PROPERTIES
    IMPORTED_LOCATION ${PDFIUM_ROOT}/lib/${ANDROID_ABI}/libpdfium.so)

# The shared engine core (#33): one implementation of the policy that used to be
# hand-written per platform. Compiled straight into this .so — no second binary
# to ship, and no ABI boundary at runtime.
#
# megapdf_structure.cpp (contract 9, #353) and megapdf_write_text.cpp (the text/Markdown
# writers over it, #355/#357) joined megapdf_core.cpp here for #386's binding; core/CMakeLists.txt
# (the desktop build) has always compiled all three together, as core/megapdf_core_internal.h's
# own comment describes.
set(CORE_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../../../../core)

add_library(megapdf_engine SHARED engine.cpp ${CORE_ROOT}/megapdf_core.cpp
    ${CORE_ROOT}/megapdf_structure.cpp ${CORE_ROOT}/megapdf_write_text.cpp)
target_include_directories(megapdf_engine PRIVATE ${PDFIUM_ROOT}/include ${CORE_ROOT})
target_link_libraries(megapdf_engine pdfium android jnigraphics log)

# How many MegaPDF patches this PDFium carries (tools/pdfium/patches, #119), the same value
# and the same reason core/CMakeLists.txt's desktop build reads it (#452): a widget's
# /Parent field-hierarchy chain is only carried across a page copy from the patch fixing
# that on, and megapdf_core.cpp, compiled straight into this .so, checks the count rather
# than assume it.
set(MEGAPDF_PDFIUM_PATCHES 0)
if(EXISTS ${PDFIUM_ROOT}/VERSION)
    file(STRINGS ${PDFIUM_ROOT}/VERSION patch_line REGEX "^MEGAPDF_PATCHES=")
    if(patch_line)
        string(REGEX REPLACE "^MEGAPDF_PATCHES=" "" MEGAPDF_PDFIUM_PATCHES "${patch_line}")
    endif()
endif()
message(STATUS "pdfium (android): MegaPDF patches: ${MEGAPDF_PDFIUM_PATCHES}")
target_compile_definitions(megapdf_engine PRIVATE MEGAPDF_PDFIUM_PATCHES=${MEGAPDF_PDFIUM_PATCHES})
# Android 15 requirement: 16 KB-aligned load segments (Play rejects 4 KB-only).
# The PDFium prebuilt is already 16 KB-aligned.
target_link_options(megapdf_engine PRIVATE "-Wl,-z,max-page-size=16384")
