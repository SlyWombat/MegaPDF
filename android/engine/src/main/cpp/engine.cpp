// JNI shim over the PDFium C API. Thin by design: marshalling only, no policy.
// Behavior mirrors the desktop reference (src/MegaPDF.Core/Engine/Pdfium/) —
// see SDD §6.2 for the cross-platform contracts.
//
// Threading: PDFium is not thread-safe. Every entry point here must be called
// from the single engine thread owned by the Kotlin PdfEngine dispatcher.

#include <jni.h>
#include <android/bitmap.h>

#include <cstring>
#include <vector>

#include "fpdfview.h"
#include "fpdf_formfill.h"
#include "fpdf_save.h"

namespace {

struct Document {
    std::vector<uint8_t> data;  // FPDF_LoadMemDocument64 requires the buffer to outlive the doc.
    FPDF_DOCUMENT doc = nullptr;
    FPDF_FORMHANDLE form = nullptr;
    FPDF_FORMFILLINFO ffi = {};
};

struct Page {
    FPDF_PAGE page = nullptr;
    Document* owner = nullptr;
};

// --- FPDF_FORMFILLINFO no-op callbacks (no JS, no XFA), as on desktop. ---
void FfiInvalidate(FPDF_FORMFILLINFO*, FPDF_PAGE, double, double, double, double) {}
void FfiOutputSelectedRect(FPDF_FORMFILLINFO*, FPDF_PAGE, double, double, double, double) {}
void FfiSetCursor(FPDF_FORMFILLINFO*, int) {}
int FfiSetTimer(FPDF_FORMFILLINFO*, int, TimerCallback) { return 0; }
void FfiKillTimer(FPDF_FORMFILLINFO*, int) {}
FPDF_SYSTEMTIME FfiGetLocalTime(FPDF_FORMFILLINFO*) { return FPDF_SYSTEMTIME{}; }
void FfiOnChange(FPDF_FORMFILLINFO*) {}
FPDF_PAGE FfiGetPage(FPDF_FORMFILLINFO*, FPDF_DOCUMENT, int) { return nullptr; }
FPDF_PAGE FfiGetCurrentPage(FPDF_FORMFILLINFO*, FPDF_DOCUMENT) { return nullptr; }
int FfiGetRotation(FPDF_FORMFILLINFO*, FPDF_PAGE) { return 0; }
void FfiExecuteNamedAction(FPDF_FORMFILLINFO*, FPDF_BYTESTRING) {}
void FfiSetTextFieldFocus(FPDF_FORMFILLINFO*, FPDF_WIDESTRING, FPDF_DWORD, FPDF_BOOL) {}
void FfiDoURIAction(FPDF_FORMFILLINFO*, FPDF_BYTESTRING) {}
void FfiDoGoToAction(FPDF_FORMFILLINFO*, int, int, float*, int) {}

void InitFormFillInfo(FPDF_FORMFILLINFO* ffi) {
    std::memset(ffi, 0, sizeof(*ffi));
    ffi->version = 1;
    ffi->FFI_Invalidate = FfiInvalidate;
    ffi->FFI_OutputSelectedRect = FfiOutputSelectedRect;
    ffi->FFI_SetCursor = FfiSetCursor;
    ffi->FFI_SetTimer = FfiSetTimer;
    ffi->FFI_KillTimer = FfiKillTimer;
    ffi->FFI_GetLocalTime = FfiGetLocalTime;
    ffi->FFI_OnChange = FfiOnChange;
    ffi->FFI_GetPage = FfiGetPage;
    ffi->FFI_GetCurrentPage = FfiGetCurrentPage;
    ffi->FFI_GetRotation = FfiGetRotation;
    ffi->FFI_ExecuteNamedAction = FfiExecuteNamedAction;
    ffi->FFI_SetTextFieldFocus = FfiSetTextFieldFocus;
    ffi->FFI_DoURIAction = FfiDoURIAction;
    ffi->FFI_DoGoToAction = FfiDoGoToAction;
}

constexpr int kRenderFlags = FPDF_ANNOT | FPDF_LCD_TEXT | FPDF_REVERSE_BYTE_ORDER;

// FPDF_FILEWRITE bridging FPDF_SaveAsCopy blocks to a java.io.OutputStream.
struct StreamWriter {
    FPDF_FILEWRITE fw;  // must be first: PDFium hands us fw*, we downcast.
    JNIEnv* env;
    jobject stream;
    jmethodID write;
    bool failed;
};

int WriteBlock(FPDF_FILEWRITE* self, const void* data, unsigned long size) {
    auto* w = reinterpret_cast<StreamWriter*>(self);
    if (w->failed) return 0;
    if (size == 0) return 1;
    JNIEnv* env = w->env;
    jbyteArray buf = env->NewByteArray(static_cast<jsize>(size));
    if (buf == nullptr) { w->failed = true; return 0; }
    env->SetByteArrayRegion(buf, 0, static_cast<jsize>(size),
                            reinterpret_cast<const jbyte*>(data));
    env->CallVoidMethod(w->stream, w->write, buf, 0, static_cast<jint>(size));
    env->DeleteLocalRef(buf);
    if (env->ExceptionCheck()) {
        // Leave the exception pending; Kotlin sees it when nativeSave returns.
        w->failed = true;
        return 0;
    }
    return 1;
}

}  // namespace

extern "C" {

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeInit(JNIEnv*, jobject) {
    FPDF_InitLibrary();
}

JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpen(JNIEnv* env, jobject, jbyteArray bytes,
                                                jstring password) {
    auto* d = new Document();
    const jsize len = env->GetArrayLength(bytes);
    d->data.resize(static_cast<size_t>(len));
    env->GetByteArrayRegion(bytes, 0, len, reinterpret_cast<jbyte*>(d->data.data()));

    const char* pw = password ? env->GetStringUTFChars(password, nullptr) : nullptr;
    d->doc = FPDF_LoadMemDocument64(d->data.data(), d->data.size(), pw);
    if (pw) env->ReleaseStringUTFChars(password, pw);

    if (d->doc == nullptr) {
        delete d;
        return 0;
    }
    InitFormFillInfo(&d->ffi);
    d->form = FPDFDOC_InitFormFillEnvironment(d->doc, &d->ffi);
    return reinterpret_cast<jlong>(d);
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeLastError(JNIEnv*, jobject) {
    return static_cast<jint>(FPDF_GetLastError());
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeCloseDocument(JNIEnv*, jobject, jlong handle) {
    auto* d = reinterpret_cast<Document*>(handle);
    if (d->form) FPDFDOC_ExitFormFillEnvironment(d->form);
    if (d->doc) FPDF_CloseDocument(d->doc);
    delete d;
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageCount(JNIEnv*, jobject, jlong handle) {
    return FPDF_GetPageCount(reinterpret_cast<Document*>(handle)->doc);
}

JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenPage(JNIEnv*, jobject, jlong handle, jint index) {
    auto* d = reinterpret_cast<Document*>(handle);
    FPDF_PAGE page = FPDF_LoadPage(d->doc, index);
    if (page == nullptr) return 0;
    if (d->form) FORM_OnAfterLoadPage(page, d->form);
    auto* p = new Page{page, d};
    return reinterpret_cast<jlong>(p);
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeClosePage(JNIEnv*, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    if (p->owner->form) FORM_OnBeforeClosePage(p->page, p->owner->form);
    FPDF_ClosePage(p->page);
    delete p;
}

JNIEXPORT jdouble JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageWidth(JNIEnv*, jobject, jlong handle) {
    return FPDF_GetPageWidth(reinterpret_cast<Page*>(handle)->page);
}

JNIEXPORT jdouble JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageHeight(JNIEnv*, jobject, jlong handle) {
    return FPDF_GetPageHeight(reinterpret_cast<Page*>(handle)->page);
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRenderPage(JNIEnv* env, jobject, jlong handle,
                                                      jobject bitmap) {
    auto* p = reinterpret_cast<Page*>(handle);

    AndroidBitmapInfo info;
    if (AndroidBitmap_getInfo(env, bitmap, &info) != ANDROID_BITMAP_RESULT_SUCCESS ||
        info.format != ANDROID_BITMAP_FORMAT_RGBA_8888) {
        return JNI_FALSE;
    }
    void* pixels = nullptr;
    if (AndroidBitmap_lockPixels(env, bitmap, &pixels) != ANDROID_BITMAP_RESULT_SUCCESS) {
        return JNI_FALSE;
    }

    const int w = static_cast<int>(info.width);
    const int h = static_cast<int>(info.height);
    FPDF_BITMAP bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, pixels,
                                          static_cast<int>(info.stride));
    if (bmp == nullptr) {
        AndroidBitmap_unlockPixels(env, bitmap);
        return JNI_FALSE;
    }
    // White ground, then page content, then live form-field values — the desktop
    // render path. FPDF_REVERSE_BYTE_ORDER makes PDFium emit RGBA to match the
    // ARGB_8888 buffer's native byte order.
    FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
    FPDF_RenderPageBitmap(bmp, p->page, 0, 0, w, h, 0, kRenderFlags);
    if (p->owner->form) FPDF_FFLDraw(p->owner->form, bmp, p->page, 0, 0, w, h, 0, kRenderFlags);
    FPDFBitmap_Destroy(bmp);

    AndroidBitmap_unlockPixels(env, bitmap);
    return JNI_TRUE;
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSave(JNIEnv* env, jobject, jlong handle,
                                                jobject outputStream) {
    auto* d = reinterpret_cast<Document*>(handle);
    if (d->form) FORM_ForceToKillFocus(d->form);

    jclass streamClass = env->GetObjectClass(outputStream);
    jmethodID write = env->GetMethodID(streamClass, "write", "([BII)V");
    if (write == nullptr) return JNI_FALSE;

    StreamWriter writer{};
    writer.fw.version = 1;
    writer.fw.WriteBlock = WriteBlock;
    writer.env = env;
    writer.stream = outputStream;
    writer.write = write;
    writer.failed = false;

    const FPDF_BOOL ok = FPDF_SaveAsCopy(d->doc, &writer.fw, 0);
    return (ok && !writer.failed) ? JNI_TRUE : JNI_FALSE;
}

}  // extern "C"
