#include <jni.h>

// Scaffold stub proving the JNI toolchain end to end; #13 replaces this with
// the PDFium shim (FPDF_InitLibrary, FPDF_LoadMemDocument, render, forms, save).
extern "C" JNIEXPORT jstring JNICALL
Java_com_megapdf_engine_PdfEngine_nativeScaffoldVersion(JNIEnv* env, jobject /*thiz*/) {
    return env->NewStringUTF("scaffold-0.1.0");
}
