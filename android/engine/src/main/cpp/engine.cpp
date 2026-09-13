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

// pdfium reports page content in user space, whose origin is the MediaBox, but it
// renders and measures the CropBox. Where the two differ -- imposed pages, trimmed
// scans -- every coordinate handed to the UI is out by that difference, so search
// highlights and tap targets land on the wrong part of the page (#28). Everything
// crossing the JNI boundary is shifted into crop-relative space, which is a no-op on
// the usual page whose crop origin is already (0,0). The core owns the origin; the
// contracts it has absorbed return crop space already, and the ones still bound
// directly here shift through this.
struct CropOrigin {
    double x = 0;
    double y = 0;
};

CropOrigin cropOrigin(const Page* p) {
    CropOrigin c;
    megapdf_page_crop_origin(p->core, &c.x, &c.y);
    return c;
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
// ---- Added text (#34) -------------------------------------------------------
// A MegaPDF text box is a page text object carrying the "MegaPDFTextBox"
// page-object mark -- the desktop's representation exactly (PdfiumEngine
// .AppendTextBox), so a box added here is a movable text box on Windows. The
// mark also carries an "id" string param: page-object indices shift as objects
// come and go, so every reversible edit addresses its target by id instead.
constexpr const char* kTextBoxMark = "MegaPDFTextBox";
constexpr const char* kTextBoxIdKey = "id";
// The face the user picked (#43), carried as a mark param beside the id rather
// than read back off the font resource: pdfium is free to normalise a standard
// font's reported name, and the cross-platform contract has to be exactly what
// was chosen. A box with no `font` param is Helvetica -- which is what every box
// written before #43 is.
constexpr const char* kTextBoxFontKey = "font";
constexpr const char* kDefaultTextBoxFont = "Helvetica";
// Handle given to a marked box that carries no id; the object index follows.
constexpr const char* kUntaggedPrefix = "text:untagged#";

bool MarkNameIs(FPDF_PAGEOBJECTMARK mark, const char* name) {
    unsigned long bytes = 0;
    if (!FPDFPageObjMark_GetName(mark, nullptr, 0, &bytes) || bytes <= 2) return false;
    std::vector<FPDF_WCHAR> buf(bytes / 2);
    if (!FPDFPageObjMark_GetName(mark, buf.data(), bytes, &bytes)) return false;
    const size_t n = buf.size() - 1;  // drop the UTF-16 terminator
    for (size_t i = 0; i < n; i++) {
        if (name[i] == '\0' || static_cast<FPDF_WCHAR>(name[i]) != buf[i]) return false;
    }
    return name[n] == '\0';
}

// True when `obj` is one of our text boxes; fills `out` with its id.
//
// A box written before the id param existed (shipping Windows 1.6.x) still reads
// as a text box, but its handle can only be its position -- and it must be
// *unique*, or a document carrying two of them would let removeTextBox delete an
// arbitrary one. Position-derived handles are not stable across edits, which is
// fine: nothing here creates untagged boxes, it only has to read them coherently.
bool TextBoxId(FPDF_PAGEOBJECT obj, int objectIndex, std::vector<jchar>* out) {
    if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) return false;
    const int marks = FPDFPageObj_CountMarks(obj);
    for (int m = 0; m < marks; m++) {
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_GetMark(obj, static_cast<unsigned long>(m));
        if (mark == nullptr || !MarkNameIs(mark, kTextBoxMark)) continue;
        unsigned long bytes = 0;
        if (FPDFPageObjMark_GetParamStringValue(mark, kTextBoxIdKey, nullptr, 0, &bytes) &&
            bytes > 2) {
            std::vector<FPDF_WCHAR> buf(bytes / 2);
            if (FPDFPageObjMark_GetParamStringValue(mark, kTextBoxIdKey, buf.data(), bytes,
                                                    &bytes)) {
                *out = std::vector<jchar>(buf.begin(), buf.end() - 1);
                return true;
            }
        }
        const std::string fallback = std::string(kUntaggedPrefix) + std::to_string(objectIndex);
        *out = std::vector<jchar>(fallback.begin(), fallback.end());
        return true;
    }
    return false;
}

// Reads a string param off the object's MegaPDFTextBox mark. False when the
// object is not one of ours, or the mark does not carry that key.
bool TextBoxMarkParam(FPDF_PAGEOBJECT obj, const char* key, std::vector<jchar>* out) {
    if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) return false;
    const int marks = FPDFPageObj_CountMarks(obj);
    for (int m = 0; m < marks; m++) {
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_GetMark(obj, static_cast<unsigned long>(m));
        if (mark == nullptr || !MarkNameIs(mark, kTextBoxMark)) continue;
        unsigned long bytes = 0;
        if (!FPDFPageObjMark_GetParamStringValue(mark, key, nullptr, 0, &bytes) || bytes <= 2) {
            return false;
        }
        std::vector<FPDF_WCHAR> buf(bytes / 2);
        if (!FPDFPageObjMark_GetParamStringValue(mark, key, buf.data(), bytes, &bytes)) {
            return false;
        }
        *out = std::vector<jchar>(buf.begin(), buf.end() - 1);
        return true;
    }
    return false;
}

// The face a box was written in, defaulting to Helvetica for boxes that predate
// the `font` param.
std::vector<jchar> TextBoxFont(FPDF_PAGEOBJECT obj) {
    std::vector<jchar> face;
    if (TextBoxMarkParam(obj, kTextBoxFontKey, &face)) return face;
    const std::string fallback = kDefaultTextBoxFont;
    return std::vector<jchar>(fallback.begin(), fallback.end());
}

FPDF_PAGEOBJECT FindTextBox(FPDF_PAGE page, const std::vector<jchar>& id) {
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        std::vector<jchar> found;
        if (obj != nullptr && TextBoxId(obj, i, &found) && found == id) return obj;
    }
    return nullptr;
}

std::vector<jchar> JavaChars(JNIEnv* env, jstring s) {
    const jchar* chars = env->GetStringChars(s, nullptr);
    const jsize len = env->GetStringLength(s);
    std::vector<jchar> out(chars, chars + len);
    env->ReleaseStringChars(s, chars);
    return out;
}

std::vector<jchar> ReadTextObjectText(FPDF_PAGEOBJECT obj, FPDF_TEXTPAGE textPage) {
    // Despite the header saying FPDF_WCHARs, the length is in BYTES (including
    // the UTF-16 NUL) -- the pdfium 152 quirk the desktop engine documents too.
    const unsigned long bytes = FPDFTextObj_GetText(obj, textPage, nullptr, 0);
    if (bytes <= 2) return {};
    std::vector<FPDF_WCHAR> buf(bytes / 2);
    FPDFTextObj_GetText(obj, textPage, buf.data(), bytes);
    return std::vector<jchar>(buf.begin(), buf.end() - 1);
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
    const std::vector<jchar> wide = JavaChars(env, id);
    return megapdf_add_check_mark(p->core, &square, MEGAPDF_MARK_CROSS, wide.data()) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

// ---- Added text (#34) -------------------------------------------------------

// Places `text` with its baseline starting at crop-space (x, y) in the named
// base-14 face, tagged with the MegaPDFTextBox mark and the given id.
//
// `fontName` must be a name FPDFText_LoadStandardFont accepts -- deliberately
// strict: the app passes one of three constants, so anything else is a bug and
// should fail loudly rather than silently render in the wrong face.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeAddTextBox(JNIEnv* env, jobject, jlong handle,
                                                      jstring text, jstring fontName,
                                                      jdouble fontSize,
                                                      jdouble x, jdouble y, jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    const CropOrigin crop = cropOrigin(p);
    FPDF_DOCUMENT doc = p->owner->doc;

    const char* faceUtf8 = env->GetStringUTFChars(fontName, nullptr);
    FPDF_FONT font = FPDFText_LoadStandardFont(doc, faceUtf8);
    if (font == nullptr) {
        env->ReleaseStringUTFChars(fontName, faceUtf8);
        return JNI_FALSE;
    }

    FPDF_PAGEOBJECT obj = FPDFPageObj_CreateTextObj(doc, font, static_cast<float>(fontSize));
    bool ok = obj != nullptr;

    if (ok) {
        std::vector<jchar> wide = JavaChars(env, text);
        wide.push_back(0);
        ok = FPDFText_SetText(obj, wide.data());
    }
    if (ok) {
        FS_MATRIX m{1, 0, 0, 1, static_cast<float>(x + crop.x), static_cast<float>(y + crop.y)};
        ok = FPDFPageObj_SetMatrix(obj, &m);
    }
    if (ok) {
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_AddMark(obj, kTextBoxMark);
        const char* idUtf8 = env->GetStringUTFChars(id, nullptr);
        ok = mark != nullptr &&
             FPDFPageObjMark_SetStringParam(doc, obj, mark, kTextBoxIdKey, idUtf8) &&
             FPDFPageObjMark_SetStringParam(doc, obj, mark, kTextBoxFontKey, faceUtf8);
        env->ReleaseStringUTFChars(id, idUtf8);
    }
    if (ok) {
        // Takes ownership, and frees the object itself on failure -- so from here
        // on it must not be destroyed by us.
        ok = FPDFPage_InsertObject(p->page, obj);
        ok = ok && FPDFPage_GenerateContent(p->page);
    } else if (obj != nullptr) {
        FPDFPageObj_Destroy(obj);
    }

    FPDFFont_Close(font);
    env->ReleaseStringUTFChars(fontName, faceUtf8);
    return ok ? JNI_TRUE : JNI_FALSE;
}

// The face of each box, aligned with nativeTextBoxIds.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxFonts(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<std::vector<jchar>> faces;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        std::vector<jchar> id;
        if (obj == nullptr || !TextBoxId(obj, i, &id)) continue;
        faces.push_back(TextBoxFont(obj));
    }
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(static_cast<jsize>(faces.size()), stringClass, nullptr);
    for (size_t i = 0; i < faces.size(); i++) {
        jstring s = env->NewString(faces[i].data(), static_cast<jsize>(faces[i].size()));
        env->SetObjectArrayElement(out, static_cast<jsize>(i), s);
        env->DeleteLocalRef(s);
    }
    return out;
}

// Ids of the MegaPDF text boxes on the page, in page-object order.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxIds(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<std::vector<jchar>> ids;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        std::vector<jchar> id;
        if (obj != nullptr && TextBoxId(obj, i, &id)) ids.push_back(id);
    }
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(static_cast<jsize>(ids.size()), stringClass, nullptr);
    for (size_t i = 0; i < ids.size(); i++) {
        jstring s = env->NewString(ids[i].data(), static_cast<jsize>(ids[i].size()));
        env->SetObjectArrayElement(out, static_cast<jsize>(i), s);
        env->DeleteLocalRef(s);
    }
    return out;
}

// The text of each box, aligned with nativeTextBoxIds.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxTexts(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    FPDF_TEXTPAGE textPage = FPDFText_LoadPage(p->page);
    std::vector<std::vector<jchar>> texts;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        std::vector<jchar> id;
        if (obj == nullptr || !TextBoxId(obj, i, &id)) continue;
        texts.push_back(textPage != nullptr ? ReadTextObjectText(obj, textPage)
                                            : std::vector<jchar>());
    }
    if (textPage != nullptr) FPDFText_ClosePage(textPage);

    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(static_cast<jsize>(texts.size()), stringClass, nullptr);
    for (size_t i = 0; i < texts.size(); i++) {
        jstring s = env->NewString(texts[i].data(), static_cast<jsize>(texts[i].size()));
        env->SetObjectArrayElement(out, static_cast<jsize>(i), s);
        env->DeleteLocalRef(s);
    }
    return out;
}

// [l, b, r, t, fontSize] per text box, aligned with nativeTextBoxIds.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextBoxRectsPacked(JNIEnv* env, jobject,
                                                              jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const CropOrigin crop = cropOrigin(p);
    std::vector<double> packed;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        std::vector<jchar> id;
        if (obj == nullptr || !TextBoxId(obj, i, &id)) continue;
        float l = 0, b = 0, r = 0, t = 0, size = 0;
        FPDFPageObj_GetBounds(obj, &l, &b, &r, &t);
        FPDFTextObj_GetFontSize(obj, &size);
        packed.push_back(l - crop.x);
        packed.push_back(b - crop.y);
        packed.push_back(r - crop.x);
        packed.push_back(t - crop.y);
        packed.push_back(size);
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
    const CropOrigin crop = cropOrigin(p);
    FPDF_PAGEOBJECT obj = FindTextBox(p->page, JavaChars(env, id));
    if (obj == nullptr) return JNI_FALSE;

    float l = 0, b = 0, r = 0, t = 0;
    FS_MATRIX m;
    if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) || !FPDFPageObj_GetMatrix(obj, &m)) {
        return JNI_FALSE;
    }
    m.e += static_cast<float>(x + crop.x) - l;
    m.f += static_cast<float>(y + crop.y) - b;
    const bool ok = FPDFPageObj_SetMatrix(obj, &m) && FPDFPage_GenerateContent(p->page);
    return ok ? JNI_TRUE : JNI_FALSE;
}

// Removes the box with the given id. Already gone counts as success, so an undo
// that races a re-render cannot fail.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRemoveTextBox(JNIEnv* env, jobject, jlong handle,
                                                         jstring id) {
    auto* p = reinterpret_cast<Page*>(handle);
    FPDF_PAGEOBJECT obj = FindTextBox(p->page, JavaChars(env, id));
    if (obj == nullptr) return JNI_TRUE;
    if (!FPDFPage_RemoveObject(p->page, obj)) return JNI_FALSE;
    FPDFPageObj_Destroy(obj);
    return FPDFPage_GenerateContent(p->page) ? JNI_TRUE : JNI_FALSE;
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
    const std::vector<jchar> wide = JavaChars(env, id);
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
