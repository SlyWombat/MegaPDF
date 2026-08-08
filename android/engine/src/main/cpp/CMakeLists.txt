cmake_minimum_required(VERSION 3.22.1)
project(megapdf_engine CXX)

# Scaffold stub. #13 replaces this with the real JNI shim linking the PDFium
# prebuilts from libs/pdfium/android/<abi>/libpdfium.so.
add_library(megapdf_engine SHARED engine.cpp)

find_library(log-lib log)
target_link_libraries(megapdf_engine ${log-lib})
