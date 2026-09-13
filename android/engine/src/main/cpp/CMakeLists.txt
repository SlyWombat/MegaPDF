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
set(CORE_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../../../../core)

add_library(megapdf_engine SHARED engine.cpp ${CORE_ROOT}/megapdf_core.cpp)
target_include_directories(megapdf_engine PRIVATE ${PDFIUM_ROOT}/include ${CORE_ROOT})
target_link_libraries(megapdf_engine pdfium android jnigraphics log)
# Android 15 requirement: 16 KB-aligned load segments (Play rejects 4 KB-only).
# The PDFium prebuilt is already 16 KB-aligned.
target_link_options(megapdf_engine PRIVATE "-Wl,-z,max-page-size=16384")
