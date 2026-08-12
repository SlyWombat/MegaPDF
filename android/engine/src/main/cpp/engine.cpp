// JNI shim over the PDFium C API. Thin by design: marshalling only, no policy.
// Behavior mirrors the desktop reference (src/MegaPDF.Core/Engine/Pdfium/) —
// see SDD §6.2 for the cross-platform contracts.
//
// Threading: PDFium is not thread-safe. Every entry point here must be called
// from the single engine thread owned by the Kotlin PdfEngine dispatcher.

#include <jni.h>
#include <android/bitmap.h>

#include <cstdint>
#include <cstring>
#include <vector>

#include "fpdfview.h"
#include "fpdf_annot.h"
#include "fpdf_edit.h"
#include "fpdf_formfill.h"
#include "fpdf_save.h"
#include "fpdf_text.h"

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

// --- Checkbox surface (#15). Behavioral reference: PdfiumEngine.cs; the
// --- heuristic constants and MegaPDF_Id tagging are SDD §6.2 contracts.

namespace {

constexpr char kMegaPdfIdKey[] = "MegaPDF_Id";

// Reads MegaPDF_Id from an annot; empty string when absent.
std::vector<jchar> ReadMegaPdfId(FPDF_ANNOTATION annot) {
    unsigned long bytes = FPDFAnnot_GetStringValue(annot, kMegaPdfIdKey, nullptr, 0);
    if (bytes <= 2) return {};
    std::vector<FPDF_WCHAR> buf(bytes / 2);
    FPDFAnnot_GetStringValue(annot, kMegaPdfIdKey, buf.data(), bytes);
    return std::vector<jchar>(buf.begin(), buf.end() - 1);  // drop the terminator
}

}  // namespace

extern "C" {

// Widget checkbox/radio fields, packed [type, checked, l, b, r, t] per field.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeFormFieldsPacked(JNIEnv* env, jobject,
                                                            jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    FPDF_FORMHANDLE form = p->owner->form;
    std::vector<double> packed;
    if (form != nullptr) {
        const int count = FPDFPage_GetAnnotCount(p->page);
        for (int i = 0; i < count; i++) {
            FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, i);
            if (annot == nullptr) continue;
            if (FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_WIDGET) {
                const int type = FPDFAnnot_GetFormFieldType(form, annot);
                if (type == FPDF_FORMFIELD_CHECKBOX || type == FPDF_FORMFIELD_RADIOBUTTON) {
                    FS_RECTF r;
                    if (FPDFAnnot_GetRect(annot, &r)) {
                        packed.push_back(type);
                        packed.push_back(FPDFAnnot_IsChecked(form, annot) ? 1 : 0);
                        packed.push_back(r.left);
                        packed.push_back(r.bottom);
                        packed.push_back(r.right);
                        packed.push_back(r.top);
                    }
                }
            }
            FPDFPage_CloseAnnot(annot);
        }
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Simulated click in page coordinates (PDF bottom-left origin) — toggles the
// field under the point through PDFium's form machinery, keeping /V, /AS and
// radio-group siblings consistent. Desktop: PdfiumEngine.ToggleCheckbox.
JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeClickAt(JNIEnv*, jobject, jlong handle,
                                                   jdouble x, jdouble y) {
    auto* p = reinterpret_cast<Page*>(handle);
    FPDF_FORMHANDLE form = p->owner->form;
    if (form == nullptr) return;
    FORM_OnLButtonDown(form, p->page, 0, x, y);
    FORM_OnLButtonUp(form, p->page, 0, x, y);
    FORM_ForceToKillFocus(form);
}

// Drawn-checkbox candidates, packed [l, b, r, t] per square. Contract constants:
// stroked-not-filled paths, 6-24pt on both axes, squareness within 25%.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeDetectSquaresPacked(JNIEnv* env, jobject,
                                                               jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<double> packed;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_PATH) continue;
        float l, b, r, t;
        if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) continue;
        const float w = r - l, h = t - b;
        if (w < 6 || w > 24 || h < 6 || h > 24) continue;
        const float larger = w > h ? w : h;
        if ((w > h ? w - h : h - w) > 0.25f * larger) continue;
        int fillmode = 0;
        FPDF_BOOL stroke = 0;
        if (!FPDFPath_GetDrawMode(obj, &fillmode, &stroke)) continue;
        if (!stroke || fillmode != FPDF_FILLMODE_NONE) continue;
        packed.push_back(l);
        packed.push_back(b);
        packed.push_back(r);
        packed.push_back(t);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Adds an X check-mark stamp annot over a drawn square, inset 10%, stroke
// 0x202020 at width max(1.2, w*0.11), tagged MegaPDF_Id = id ("mark:...").
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddCheckMark(JNIEnv* env, jobject, jlong handle,
                                                        jdouble l, jdouble b, jdouble r,
                                                        jdouble t, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    const double w = r - l, h = t - b;
    const float il = static_cast<float>(l + 0.10 * w);
    const float ib = static_cast<float>(b + 0.10 * h);
    const float ir = static_cast<float>(r - 0.10 * w);
    const float it = static_cast<float>(t - 0.10 * h);

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(p->page, FPDF_ANNOT_STAMP);
    if (annot == nullptr) return JNI_FALSE;

    bool ok = true;
    FS_RECTF rect{il, it, ir, ib};  // FS_RECTF is left, top, right, bottom
    ok = ok && FPDFAnnot_SetRect(annot, &rect);

    FPDF_PAGEOBJECT path = FPDFPageObj_CreateNewPath(il, ib);
    ok = ok && path != nullptr;
    if (path != nullptr) {
        ok = ok && FPDFPath_LineTo(path, ir, it);
        ok = ok && FPDFPath_MoveTo(path, il, it);
        ok = ok && FPDFPath_LineTo(path, ir, ib);
        FPDFPageObj_SetStrokeColor(path, 0x20, 0x20, 0x20, 0xFF);
        const double strokeWidth = w * 0.11 > 1.2 ? w * 0.11 : 1.2;
        FPDFPageObj_SetStrokeWidth(path, static_cast<float>(strokeWidth));
        FPDFPath_SetDrawMode(path, FPDF_FILLMODE_NONE, 1);
        ok = ok && FPDFAnnot_AppendObject(annot, path);
    }

    if (ok) {
        const jchar* chars = env->GetStringChars(id, nullptr);
        const jsize len = env->GetStringLength(id);
        std::vector<FPDF_WCHAR> wide(chars, chars + len);
        wide.push_back(0);
        env->ReleaseStringChars(id, chars);
        ok = FPDFAnnot_SetStringValue(annot, kMegaPdfIdKey, wide.data());
    }

    FPDFPage_CloseAnnot(annot);
    return ok ? JNI_TRUE : JNI_FALSE;
}

// MegaPDF_Id per annot index ("" for annots that aren't ours).
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAnnotIds(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const int count = FPDFPage_GetAnnotCount(p->page);
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(count, stringClass, nullptr);
    for (int i = 0; i < count; i++) {
        FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, i);
        std::vector<jchar> id;
        if (annot != nullptr) {
            id = ReadMegaPdfId(annot);
            FPDFPage_CloseAnnot(annot);
        }
        jstring s = env->NewString(id.data(), static_cast<jsize>(id.size()));
        env->SetObjectArrayElement(out, i, s);
        env->DeleteLocalRef(s);
    }
    return out;
}

// Annot rects packed [l, b, r, t] per annot index, aligned with nativeAnnotIds.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAnnotRectsPacked(JNIEnv* env, jobject,
                                                            jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const int count = FPDFPage_GetAnnotCount(p->page);
    std::vector<double> packed(static_cast<size_t>(count) * 4, 0.0);
    for (int i = 0; i < count; i++) {
        FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, i);
        if (annot == nullptr) continue;
        FS_RECTF r;
        if (FPDFAnnot_GetRect(annot, &r)) {
            packed[i * 4 + 0] = r.left;
            packed[i * 4 + 1] = r.bottom;
            packed[i * 4 + 2] = r.right;
            packed[i * 4 + 3] = r.top;
        }
        FPDFPage_CloseAnnot(annot);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRemoveAnnot(JNIEnv*, jobject, jlong handle,
                                                       jint index) {
    auto* p = reinterpret_cast<Page*>(handle);
    return FPDFPage_RemoveAnnot(p->page, index) ? JNI_TRUE : JNI_FALSE;
}

// --- Signature stamps (#17). Behavioral reference: PdfiumEngine.AddImageStamp.

// Places an image stamp: STAMP annot at rect, image object positioned via the
// unit-square matrix (A=width, D=height, E=left, F=bottom, PDF points), tagged
// MegaPDF_Id = id ("sig:..."). Pixels are ARGB ints (Android Bitmap layout).
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddImageStamp(JNIEnv* env, jobject, jlong handle,
                                                         jintArray pixels, jint pw, jint ph,
                                                         jdouble l, jdouble b, jdouble r,
                                                         jdouble t, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);

    FPDF_BITMAP bmp = FPDFBitmap_Create(pw, ph, /*alpha=*/1);
    if (bmp == nullptr) return JNI_FALSE;
    {
        jint* src = env->GetIntArrayElements(pixels, nullptr);
        auto* dst = static_cast<uint8_t*>(FPDFBitmap_GetBuffer(bmp));
        const int stride = FPDFBitmap_GetStride(bmp);
        for (int y = 0; y < ph; y++) {
            uint8_t* row = dst + y * stride;
            for (int x = 0; x < pw; x++) {
                const uint32_t argb = static_cast<uint32_t>(src[y * pw + x]);
                row[x * 4 + 0] = argb & 0xFF;          // B
                row[x * 4 + 1] = (argb >> 8) & 0xFF;   // G
                row[x * 4 + 2] = (argb >> 16) & 0xFF;  // R
                row[x * 4 + 3] = (argb >> 24) & 0xFF;  // A
            }
        }
        env->ReleaseIntArrayElements(pixels, src, JNI_ABORT);
    }

    bool ok = true;
    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(p->page, FPDF_ANNOT_STAMP);
    if (annot == nullptr) {
        FPDFBitmap_Destroy(bmp);
        return JNI_FALSE;
    }
    FS_RECTF rect{static_cast<float>(l), static_cast<float>(t),
                  static_cast<float>(r), static_cast<float>(b)};
    ok = ok && FPDFAnnot_SetRect(annot, &rect);

    FPDF_PAGEOBJECT img = FPDFPageObj_NewImageObj(p->owner->doc);
    ok = ok && img != nullptr;
    if (img != nullptr) {
        ok = ok && FPDFImageObj_SetBitmap(nullptr, 0, img, bmp);
        FS_MATRIX m{static_cast<float>(r - l), 0, 0,
                    static_cast<float>(t - b), static_cast<float>(l),
                    static_cast<float>(b)};
        ok = ok && FPDFPageObj_SetMatrix(img, &m);
        ok = ok && FPDFAnnot_AppendObject(annot, img);
    }

    if (ok) {
        const jchar* chars = env->GetStringChars(id, nullptr);
        const jsize len = env->GetStringLength(id);
        std::vector<FPDF_WCHAR> wide(chars, chars + len);
        wide.push_back(0);
        env->ReleaseStringChars(id, chars);
        ok = FPDFAnnot_SetStringValue(annot, kMegaPdfIdKey, wide.data());
    }

    FPDFPage_CloseAnnot(annot);
    FPDFBitmap_Destroy(bmp);
    return ok ? JNI_TRUE : JNI_FALSE;
}

// Reads the stamp's image back at native pixel resolution: temporarily set a
// 1pt-per-pixel matrix, render, restore (the desktop move/resize pattern, so
// repeated moves never lose resolution). Returns [width, height, argb...] or
// null when the annot has no image object.
JNIEXPORT jintArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeGetStampImagePacked(JNIEnv* env, jobject,
                                                               jlong handle,
                                                               jint annotIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, annotIndex);
    if (annot == nullptr) return nullptr;

    jintArray result = nullptr;
    const int objCount = FPDFAnnot_GetObjectCount(annot);
    for (int i = 0; i < objCount && result == nullptr; i++) {
        FPDF_PAGEOBJECT obj = FPDFAnnot_GetObject(annot, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) continue;

        unsigned int pw = 0, ph = 0;
        if (!FPDFImageObj_GetImagePixelSize(obj, &pw, &ph) || pw == 0 || ph == 0) continue;

        FS_MATRIX original;
        if (!FPDFPageObj_GetMatrix(obj, &original)) continue;
        FS_MATRIX native{static_cast<float>(pw), 0, 0, static_cast<float>(ph), 0, 0};
        FPDFPageObj_SetMatrix(obj, &native);
        FPDF_BITMAP bmp = FPDFImageObj_GetRenderedBitmap(p->owner->doc, p->page, obj);
        FPDFPageObj_SetMatrix(obj, &original);
        if (bmp == nullptr) continue;

        const int w = FPDFBitmap_GetWidth(bmp);
        const int h = FPDFBitmap_GetHeight(bmp);
        const int stride = FPDFBitmap_GetStride(bmp);
        const auto* buf = static_cast<const uint8_t*>(FPDFBitmap_GetBuffer(bmp));
        std::vector<jint> packed(static_cast<size_t>(w) * h + 2);
        packed[0] = w;
        packed[1] = h;
        for (int y = 0; y < h; y++) {
            const uint8_t* row = buf + y * stride;
            for (int x = 0; x < w; x++) {
                const uint32_t bgra = row[x * 4 + 0] | (row[x * 4 + 1] << 8) |
                                      (row[x * 4 + 2] << 16) |
                                      (static_cast<uint32_t>(row[x * 4 + 3]) << 24);
                // BGRA bytes -> ARGB int: A stays, swap R/B positions.
                packed[2 + y * w + x] = static_cast<jint>(
                    (bgra & 0xFF000000u) | ((bgra & 0x00FF0000u) >> 16) |
                    (bgra & 0x0000FF00u) | ((bgra & 0x000000FFu) << 16));
            }
        }
        FPDFBitmap_Destroy(bmp);

        result = env->NewIntArray(static_cast<jsize>(packed.size()));
        if (result != nullptr) {
            env->SetIntArrayRegion(result, 0, static_cast<jsize>(packed.size()),
                                   packed.data());
        }
    }
    FPDFPage_CloseAnnot(annot);
    return result;
}

}  // extern "C"

// --- Text search (#26). Case-insensitive literal substring search — flags 0,
// --- no whole-word, no regex; the contract is shared across all platforms.

extern "C" {

// Matches on the page, packed [rectCount, l, b, r, t...] per match (a match
// wrapping across lines has several rects), PDF points, bottom-left origin.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSearchPagePacked(JNIEnv* env, jobject,
                                                            jlong handle, jstring query) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<double> packed;
    const jsize len = env->GetStringLength(query);
    FPDF_TEXTPAGE text = len > 0 ? FPDFText_LoadPage(p->page) : nullptr;
    if (text != nullptr) {
        // jchar is already UTF-16; FPDF_WIDESTRING wants a null terminator.
        const jchar* chars = env->GetStringChars(query, nullptr);
        std::vector<FPDF_WCHAR> wide(chars, chars + len);
        wide.push_back(0);
        env->ReleaseStringChars(query, chars);

        FPDF_SCHHANDLE find = FPDFText_FindStart(text, wide.data(), 0, 0);
        if (find != nullptr) {
            while (FPDFText_FindNext(find)) {
                const int start = FPDFText_GetSchResultIndex(find);
                const int count = FPDFText_GetSchCount(find);
                const int rects = FPDFText_CountRects(text, start, count);
                if (rects <= 0) continue;
                packed.push_back(rects);
                for (int i = 0; i < rects; i++) {
                    double l = 0, t = 0, r = 0, b = 0;
                    FPDFText_GetRect(text, i, &l, &t, &r, &b);
                    packed.push_back(l);
                    packed.push_back(b);
                    packed.push_back(r);
                    packed.push_back(t);
                }
            }
            FPDFText_FindClose(find);
        }
        FPDFText_ClosePage(text);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

}  // extern "C"
