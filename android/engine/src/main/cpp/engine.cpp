// JNI shim over the shared engine core and, for the contracts that have not
// migrated yet, the PDFium C API. Thin by design: marshalling only, no policy.
// Behavior mirrors the desktop reference (src/MegaPDF.Core/Engine/Pdfium/) —
// see SDD §6.2 for the cross-platform contracts.
//
// Since #105 the core owns the document: bytes go in, opaque handles come out,
// and the form-fill environment, page lifecycle and crop-origin bookkeeping live
// in core/. The Document and Page structs here wrap the core's handles and keep
// the raw FPDF_* handles beside them for the JNI functions still bound directly.
//
// Threading: PDFium is not thread-safe. The core serialises its own calls; the
// direct PDFium calls here must still come from the single engine thread owned by
// the Kotlin PdfEngine dispatcher.

#include <jni.h>
#include <android/bitmap.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "fpdfview.h"
#include "fpdf_annot.h"
#include "fpdf_edit.h"
#include "fpdf_formfill.h"
#include "fpdf_save.h"
#include "fpdf_text.h"
#include "fpdf_transformpage.h"  // FPDFPage_GetCropBox

#include "megapdf_core.h"  // the shared policy core (#33)

namespace {

struct Document {
    megapdf_document* core = nullptr;
    FPDF_DOCUMENT doc = nullptr;      // megapdf_document_raw(core), for unmigrated contracts
    FPDF_FORMHANDLE form = nullptr;   // megapdf_document_form_raw(core), likewise
};

struct Page {
    megapdf_page* core = nullptr;
    FPDF_PAGE page = nullptr;         // megapdf_page_raw(core), for unmigrated contracts
    Document* owner = nullptr;
};


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
    // The core copies the bytes, so the JNI array is only borrowed for the call.
    const jsize len = env->GetArrayLength(bytes);
    jbyte* data = env->GetByteArrayElements(bytes, nullptr);
    const char* pw = password ? env->GetStringUTFChars(password, nullptr) : nullptr;
    megapdf_document* core = megapdf_open(data, static_cast<size_t>(len), pw);
    if (pw) env->ReleaseStringUTFChars(password, pw);
    env->ReleaseByteArrayElements(bytes, data, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    d->doc = static_cast<FPDF_DOCUMENT>(megapdf_document_raw(core));
    d->form = static_cast<FPDF_FORMHANDLE>(megapdf_document_form_raw(core));
    return reinterpret_cast<jlong>(d);
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeLastError(JNIEnv*, jobject) {
    return static_cast<jint>(megapdf_last_error());
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeCloseDocument(JNIEnv*, jobject, jlong handle) {
    auto* d = reinterpret_cast<Document*>(handle);
    megapdf_close(d->core);   // form environment, any page still open, then the document
    delete d;
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageCount(JNIEnv*, jobject, jlong handle) {
    return megapdf_page_count(reinterpret_cast<Document*>(handle)->core);
}

JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenPage(JNIEnv*, jobject, jlong handle, jint index) {
    auto* d = reinterpret_cast<Document*>(handle);
    megapdf_page* core = megapdf_load_page(d->core, index);   // FORM_OnAfterLoadPage inside
    if (core == nullptr) return 0;
    auto* p = new Page();
    p->core = core;
    p->page = static_cast<FPDF_PAGE>(megapdf_page_raw(core));
    p->owner = d;
    return reinterpret_cast<jlong>(p);
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeClosePage(JNIEnv*, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_close_page(p->core);   // FORM_OnBeforeClosePage + FPDF_ClosePage inside
    delete p;
}

JNIEXPORT jdouble JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageWidth(JNIEnv*, jobject, jlong handle) {
    return megapdf_page_width(reinterpret_cast<Page*>(handle)->core);
}

JNIEXPORT jdouble JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageHeight(JNIEnv*, jobject, jlong handle) {
    return megapdf_page_height(reinterpret_cast<Page*>(handle)->core);
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
    megapdf_form_commit(d->core);   // commit any in-progress field edit (#107)

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


// Reads MegaPDF_Id from an annot; empty string when absent.
// ---- Added text (#34): the MegaPDFTextBox representation is written and read by
// the core (#109); this shim only marshals.

std::vector<jchar> JavaChars(JNIEnv* env, jstring s) {
    const jchar* chars = env->GetStringChars(s, nullptr);
    const jsize len = env->GetStringLength(s);
    std::vector<jchar> out(chars, chars + len);
    env->ReleaseStringChars(s, chars);
    return out;
}

}  // namespace

extern "C" {

// Widget checkbox/radio fields, packed [type, checked, l, b, r, t] per field —
// read through the core's form environment (#107); the checkbox/radio filter is
// this platform's choice of what to surface.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeFormFieldsPacked(JNIEnv* env, jobject,
                                                            jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<double> packed;
    if (megapdf_form_fields* fields = megapdf_form_fields_load(p->core)) {
        const size_t count = megapdf_form_field_count(fields);
        for (size_t i = 0; i < count; i++) {
            megapdf_form_field f{};
            if (megapdf_form_field_get(fields, i, &f) != MEGAPDF_OK) continue;
            if (f.kind != MEGAPDF_FIELD_CHECKBOX && f.kind != MEGAPDF_FIELD_RADIO) continue;
            packed.push_back(f.kind == MEGAPDF_FIELD_RADIO ? FPDF_FORMFIELD_RADIOBUTTON : FPDF_FORMFIELD_CHECKBOX);
            packed.push_back(f.is_checked ? 1 : 0);
            packed.push_back(f.bounds.left);
            packed.push_back(f.bounds.bottom);
            packed.push_back(f.bounds.right);
            packed.push_back(f.bounds.top);
        }
        megapdf_form_fields_free(fields);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Simulated click in page coordinates (PDF bottom-left origin, crop space) —
// toggles the field under the point through PDFium's form machinery, keeping
// /V, /AS and radio-group siblings consistent. One implementation, in the core.
JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeClickAt(JNIEnv*, jobject, jlong handle,
                                                   jdouble x, jdouble y) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_form_click(p->core, x, y);
}

// Drawn-checkbox candidates, packed [l, b, r, t] per square. Contract constants:
// stroked-not-filled paths, 6-24pt on both axes, squareness within 25%.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeDetectSquaresPacked(JNIEnv* env, jobject,
                                                                jlong handle) {
    // The heuristic itself lives in the shared core (#33) — this is marshalling
    // only, which is what this shim was always supposed to be.
    auto* p = reinterpret_cast<Page*>(handle);
    const size_t count = megapdf_detect_checkbox_squares(p->core, nullptr, 0);
    std::vector<megapdf_rect> rects(count);
    if (count > 0) megapdf_detect_checkbox_squares(p->core, rects.data(), count);

    std::vector<double> packed;
    packed.reserve(count * 4);
    for (const megapdf_rect& r : rects) {
        packed.push_back(r.left);
        packed.push_back(r.bottom);
        packed.push_back(r.right);
        packed.push_back(r.top);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Adds the ✗ check-mark stamp over a drawn square, tagged MegaPDF_Id = id
// ("mark:..."): geometry and ink are the core's (#108).
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddCheckMark(JNIEnv* env, jobject, jlong handle,
                                                        jdouble l, jdouble b, jdouble r,
                                                        jdouble t, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    const megapdf_rect square{l, b, r, t};
    std::vector<jchar> wide = JavaChars(env, id);
    wide.push_back(0);   // the core wants a NUL-terminated id
    return megapdf_add_check_mark(p->core, &square, MEGAPDF_MARK_CROSS, wide.data()) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// ---- Added text (#34) -------------------------------------------------------

// Places `text` with its baseline starting at crop-space (x, y) in the named
// base-14 face, tagged with the MegaPDFTextBox mark and the given id — all in
// the core (#109), which also rejects any face outside the three (#43).
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddTextBox(JNIEnv* env, jobject, jlong handle,
                                                      jstring text, jstring fontName,
                                                      jdouble fontSize,
                                                      jdouble x, jdouble y, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<jchar> wideText = JavaChars(env, text);
    wideText.push_back(0);
    std::vector<jchar> wideId = JavaChars(env, id);
    wideId.push_back(0);
    const char* faceUtf8 = env->GetStringUTFChars(fontName, nullptr);
    int index = -1;
    const int status = megapdf_add_text_box(p->core, -1, wideText.data(), faceUtf8, fontSize, x, y, wideId.data(), &index);
    env->ReleaseStringUTFChars(fontName, faceUtf8);
    return status == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

}  // extern "C"

namespace {

// The core's text-box listing, decoded once for the four aligned arrays below.
struct BoxRow {
    int objectIndex;
    std::vector<jchar> id;
    std::vector<jchar> font;
    std::vector<jchar> text;
    megapdf_rect bounds;
    double fontSize;
};

std::vector<jchar> CoreString(const megapdf_text* t, size_t i, megapdf_text_field field) {
    const size_t n = megapdf_text_run_string(t, i, field, nullptr, 0);
    std::vector<jchar> out(n);
    if (n > 0) megapdf_text_run_string(t, i, field, out.data(), n);
    return out;
}

std::vector<BoxRow> LoadBoxes(const Page* p) {
    std::vector<BoxRow> rows;
    megapdf_text* t = megapdf_text_load(p->core, MEGAPDF_TEXT_BOXES_ONLY);
    if (t == nullptr) return rows;
    for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
        megapdf_text_run r{};
        if (megapdf_text_run_get(t, i, &r) != MEGAPDF_OK) continue;
        BoxRow row;
        row.objectIndex = r.object_index;
        row.id = CoreString(t, i, MEGAPDF_TEXT_RUN_BOX_ID);
        if (row.id.empty()) {
            // A box written before the id param existed (shipping Windows 1.6.x): its
            // handle is its position, which the core's find understands too.
            const std::string fallback = "text:untagged#" + std::to_string(r.object_index);
            row.id.assign(fallback.begin(), fallback.end());
        }
        row.font = CoreString(t, i, MEGAPDF_TEXT_RUN_BOX_FONT);
        if (row.font.empty()) {
            const std::string fallback = "Helvetica";   // every box written before #43
            row.font.assign(fallback.begin(), fallback.end());
        }
        row.text = CoreString(t, i, MEGAPDF_TEXT_RUN_TEXT);
        row.bounds = r.bounds;
        row.fontSize = r.font_size;
        rows.push_back(std::move(row));
    }
    megapdf_text_free(t);
    return rows;
}

jobjectArray StringArray(JNIEnv* env, const std::vector<std::vector<jchar>>& items) {
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(static_cast<jsize>(items.size()), stringClass, nullptr);
    for (size_t i = 0; i < items.size(); i++) {
        jstring s = env->NewString(items[i].data(), static_cast<jsize>(items[i].size()));
        env->SetObjectArrayElement(out, static_cast<jsize>(i), s);
        env->DeleteLocalRef(s);
    }
    return out;
}

}  // namespace

extern "C" {

// The face of each box, aligned with nativeTextBoxIds.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxFonts(JNIEnv* env, jobject, jlong handle) {
    std::vector<std::vector<jchar>> items;
    for (auto& row : LoadBoxes(reinterpret_cast<Page*>(handle))) items.push_back(std::move(row.font));
    return StringArray(env, items);
}

// Ids of the MegaPDF text boxes on the page, in page-object order.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxIds(JNIEnv* env, jobject, jlong handle) {
    std::vector<std::vector<jchar>> items;
    for (auto& row : LoadBoxes(reinterpret_cast<Page*>(handle))) items.push_back(std::move(row.id));
    return StringArray(env, items);
}

// The text of each box, aligned with nativeTextBoxIds.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxTexts(JNIEnv* env, jobject, jlong handle) {
    std::vector<std::vector<jchar>> items;
    for (auto& row : LoadBoxes(reinterpret_cast<Page*>(handle))) items.push_back(std::move(row.text));
    return StringArray(env, items);
}

// [l, b, r, t, fontSize] per text box, aligned with nativeTextBoxIds.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxRectsPacked(JNIEnv* env, jobject,
                                                              jlong handle) {
    std::vector<double> packed;
    for (const auto& row : LoadBoxes(reinterpret_cast<Page*>(handle))) {
        packed.push_back(row.bounds.left);
        packed.push_back(row.bounds.bottom);
        packed.push_back(row.bounds.right);
        packed.push_back(row.bounds.top);
        packed.push_back(row.fontSize);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Translates the box so its lower-left corner lands on crop-space (x, y).
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeMoveTextBox(JNIEnv* env, jobject, jlong handle,
                                                       jstring id, jdouble x, jdouble y) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<jchar> wide = JavaChars(env, id);
    wide.push_back(0);
    const int index = megapdf_find_text_box(p->core, wide.data());
    if (index < 0) return JNI_FALSE;
    return megapdf_move_text_box(p->core, index, x, y) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// Removes the box with the given id. Already gone counts as success, so an undo
// that races a re-render cannot fail.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRemoveTextBox(JNIEnv* env, jobject, jlong handle,
                                                         jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<jchar> wide = JavaChars(env, id);
    wide.push_back(0);
    return megapdf_remove_text_box(p->core, wide.data()) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// MegaPDF_Id per annot index ("" for annots that aren't ours), from the core's
// stamp list; the array is indexed by annotation so Kotlin's Stamp.annotIndex holds.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAnnotIds(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const int count = FPDFPage_GetAnnotCount(p->page);
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(count, stringClass, nullptr);
    std::vector<std::vector<jchar>> ids(static_cast<size_t>(count));
    if (megapdf_stamps* stamps = megapdf_stamps_load(p->core)) {
        for (size_t i = 0; i < megapdf_stamp_count(stamps); i++) {
            megapdf_stamp st{};
            if (megapdf_stamp_get(stamps, i, &st) != MEGAPDF_OK || st.annot_index < 0 || st.annot_index >= count) continue;
            const size_t n = megapdf_stamp_id(stamps, i, nullptr, 0);
            std::vector<jchar> id(n);
            if (n > 0) megapdf_stamp_id(stamps, i, id.data(), n);
            ids[static_cast<size_t>(st.annot_index)] = std::move(id);
        }
        megapdf_stamps_free(stamps);
    }
    for (int i = 0; i < count; i++) {
        jstring s = env->NewString(ids[i].data(), static_cast<jsize>(ids[i].size()));
        env->SetObjectArrayElement(out, i, s);
        env->DeleteLocalRef(s);
    }
    return out;
}

// Annot rects packed [l, b, r, t] per annot index, aligned with nativeAnnotIds
// (zeros for annots that aren't ours), crop space.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAnnotRectsPacked(JNIEnv* env, jobject,
                                                            jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const int count = FPDFPage_GetAnnotCount(p->page);
    std::vector<double> packed(static_cast<size_t>(count) * 4, 0.0);
    if (megapdf_stamps* stamps = megapdf_stamps_load(p->core)) {
        for (size_t i = 0; i < megapdf_stamp_count(stamps); i++) {
            megapdf_stamp st{};
            if (megapdf_stamp_get(stamps, i, &st) != MEGAPDF_OK || st.annot_index < 0 || st.annot_index >= count) continue;
            packed[st.annot_index * 4 + 0] = st.bounds.left;
            packed[st.annot_index * 4 + 1] = st.bounds.bottom;
            packed[st.annot_index * 4 + 2] = st.bounds.right;
            packed[st.annot_index * 4 + 3] = st.bounds.top;
        }
        megapdf_stamps_free(stamps);
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
    return megapdf_remove_annotation(p->core, index) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// --- Signature stamps (#17), placed by the core (#108).

// Places an image stamp over [l, b, r, t] (crop space), tagged MegaPDF_Id = id
// ("sig:..."). Pixels are ARGB ints (Android Bitmap layout); the core takes BGRA bytes.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddImageStamp(JNIEnv* env, jobject, jlong handle,
                                                         jintArray pixels, jint pw, jint ph,
                                                         jdouble l, jdouble b, jdouble r,
                                                         jdouble t, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    if (pw <= 0 || ph <= 0) return JNI_FALSE;
    std::vector<uint8_t> bgra(static_cast<size_t>(pw) * ph * 4);
    {
        jint* src = env->GetIntArrayElements(pixels, nullptr);
        for (int y = 0; y < ph; y++) {
            for (int x = 0; x < pw; x++) {
                const uint32_t argb = static_cast<uint32_t>(src[y * pw + x]);
                uint8_t* px = &bgra[(static_cast<size_t>(y) * pw + x) * 4];
                px[0] = argb & 0xFF;          // B
                px[1] = (argb >> 8) & 0xFF;   // G
                px[2] = (argb >> 16) & 0xFF;  // R
                px[3] = (argb >> 24) & 0xFF;  // A
            }
        }
        env->ReleaseIntArrayElements(pixels, src, JNI_ABORT);
    }
    const megapdf_rect bounds{l, b, r, t};
    std::vector<jchar> wide = JavaChars(env, id);
    wide.push_back(0);   // the core wants a NUL-terminated id
    return megapdf_add_image_stamp(p->core, bgra.data(), pw, ph, &bounds, wide.data()) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// Reads the stamp's image back at native pixel resolution (the core's rule, so
// repeated moves never lose resolution). Returns [width, height, argb...] or
// null when the annot has no image object.
JNIEXPORT jintArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeGetStampImagePacked(JNIEnv* env, jobject,
                                                               jlong handle,
                                                               jint annotIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_image* img = megapdf_stamp_image_load(p->core, annotIndex);
    if (img == nullptr) return nullptr;
    const int w = megapdf_image_width(img);
    const int h = megapdf_image_height(img);
    std::vector<uint8_t> bgra(megapdf_image_pixels(img, nullptr, 0));
    megapdf_image_pixels(img, bgra.data(), bgra.size());
    megapdf_image_free(img);

    std::vector<jint> packed(static_cast<size_t>(w) * h + 2);
    packed[0] = w;
    packed[1] = h;
    for (size_t i = 0; i < static_cast<size_t>(w) * h; i++) {
        const uint8_t* px = &bgra[i * 4];
        // BGRA bytes -> ARGB int.
        packed[2 + i] = static_cast<jint>((static_cast<uint32_t>(px[3]) << 24) | (static_cast<uint32_t>(px[2]) << 16) |
                                          (static_cast<uint32_t>(px[1]) << 8) | px[0]);
    }
    jintArray result = env->NewIntArray(static_cast<jsize>(packed.size()));
    if (result != nullptr) {
        env->SetIntArrayRegion(result, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return result;
}

}  // extern "C"

// --- Text search (#26). Case-insensitive literal substring search — flags 0,
// --- no whole-word, no regex; the contract is shared across all platforms and,
// --- since #105, implemented once in the core.

extern "C" {

// Matches on the page, packed [rectCount, l, b, r, t...] per match (a match
// wrapping across lines has several rects), PDF points, bottom-left origin,
// crop-relative — the core's stream, handed through unchanged.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSearchPagePacked(JNIEnv* env, jobject,
                                                            jlong handle, jstring query) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<double> packed;
    const jsize len = env->GetStringLength(query);
    if (len > 0) {
        // jchar is already UTF-16; the core wants a NUL terminator.
        const jchar* chars = env->GetStringChars(query, nullptr);
        std::vector<unsigned short> wide(chars, chars + len);
        wide.push_back(0);
        env->ReleaseStringChars(query, chars);

        const size_t total = megapdf_search_page(p->core, wide.data(), nullptr, 0);
        packed.resize(total);
        if (total > 0) megapdf_search_page(p->core, wide.data(), packed.data(), total);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

}  // extern "C"
