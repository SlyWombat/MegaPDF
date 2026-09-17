// JNI shim over the shared engine core. Thin by design: marshalling only, no
// policy. Behaviour mirrors the desktop reference (src/MegaPDF.Core) — see SDD §6.2
// for the cross-platform contracts.
//
// Every contract lives in core/ (#105–#112): bytes go in, opaque handles come
// out, coordinates come back in crop space, and nothing here calls PDFium. The
// Document and Page structs wrap the core's handles for the Kotlin side.
//
// Threading: the core serialises its own calls; the Kotlin PdfEngine dispatcher
// keeps multi-call sequences (count, then fill) on one thread.

#include <jni.h>
#include <android/bitmap.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>


#include "megapdf_core.h"  // the shared policy core (#33)

namespace {

struct Document {
    megapdf_document* core = nullptr;
};

struct Page {
    megapdf_page* core = nullptr;
    Document* owner = nullptr;
};


// Bridges the core's save callback to a java.io.OutputStream.
struct StreamWriter {
    JNIEnv* env;
    jobject stream;
    jmethodID write;
    bool failed;
};

int WriteToStream(void* context, const void* data, size_t size) {
    auto* w = static_cast<StreamWriter*>(context);
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
    // Nothing to do: the core initialises PDFium on its first open (#105).
}

JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpen(JNIEnv* env, jobject, jbyteArray bytes,
                                                jbyteArray passwordUtf8) {
    // The core copies the bytes, so the JNI array is only borrowed for the call. The
    // password arrives as NUL-terminated UTF-8 bytes, like the security saves' (#131,
    // ADR-004 decision 9): GetStringUTFChars gives modified UTF-8, which writes characters
    // outside the BMP differently from the UTF-8 a security handler hashes.
    const jsize len = env->GetArrayLength(bytes);
    jbyte* data = env->GetByteArrayElements(bytes, nullptr);
    jbyte* pw = passwordUtf8 != nullptr ? env->GetByteArrayElements(passwordUtf8, nullptr) : nullptr;
    megapdf_document* core = megapdf_open(data, static_cast<size_t>(len), reinterpret_cast<const char*>(pw));
    if (pw != nullptr) env->ReleaseByteArrayElements(passwordUtf8, pw, JNI_ABORT);
    env->ReleaseByteArrayElements(bytes, data, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    return reinterpret_cast<jlong>(d);
}

// Opens bytes with the credentials `like` was opened with (#132): a protected document's
// saved copy is still protected, so reading it back needs the same password.
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenLike(JNIEnv* env, jobject, jlong like, jbyteArray bytes) {
    const jsize len = env->GetArrayLength(bytes);
    jbyte* data = env->GetByteArrayElements(bytes, nullptr);
    megapdf_document* core =
        megapdf_open_like(reinterpret_cast<Document*>(like)->core, data, static_cast<size_t>(len));
    env->ReleaseByteArrayElements(bytes, data, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    return reinterpret_cast<jlong>(d);
}

// Opens a document from a descriptor, read on demand for its whole life (#147, #148): a
// content URI has a descriptor but no path. The core owns `fd` from here, and closes it
// whether or not the open succeeds.
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenFd(JNIEnv* env, jobject, jint fd, jbyteArray passwordUtf8) {
    jbyte* pw = passwordUtf8 != nullptr ? env->GetByteArrayElements(passwordUtf8, nullptr) : nullptr;
    megapdf_document* core = megapdf_open_fd(static_cast<int>(fd), reinterpret_cast<const char*>(pw));
    if (pw != nullptr) env->ReleaseByteArrayElements(passwordUtf8, pw, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    return reinterpret_cast<jlong>(d);
}

// Opens a document from a file path, read on demand (#147, #148). The path arrives as
// NUL-terminated UTF-8 bytes for the same reason the password does.
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenFile(JNIEnv* env, jobject, jbyteArray pathUtf8, jbyteArray passwordUtf8) {
    jbyte* path = env->GetByteArrayElements(pathUtf8, nullptr);
    jbyte* pw = passwordUtf8 != nullptr ? env->GetByteArrayElements(passwordUtf8, nullptr) : nullptr;
    megapdf_document* core = megapdf_open_file(reinterpret_cast<const char*>(path), reinterpret_cast<const char*>(pw));
    if (pw != nullptr) env->ReleaseByteArrayElements(passwordUtf8, pw, JNI_ABORT);
    env->ReleaseByteArrayElements(pathUtf8, path, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    return reinterpret_cast<jlong>(d);
}

// nativeOpenFile with the credentials `like` was opened with (#132, #148).
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeOpenFileLike(JNIEnv* env, jobject, jlong like, jbyteArray pathUtf8) {
    jbyte* path = env->GetByteArrayElements(pathUtf8, nullptr);
    megapdf_document* core =
        megapdf_open_file_like(reinterpret_cast<Document*>(like)->core, reinterpret_cast<const char*>(path));
    env->ReleaseByteArrayElements(pathUtf8, path, JNI_ABORT);
    if (core == nullptr) return 0;

    auto* d = new Document();
    d->core = core;
    return reinterpret_cast<jlong>(d);
}

// Whether the document reads the file `fd` is open on (#147); `fd` stays the caller's.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeReadsFd(JNIEnv*, jobject, jlong handle, jint fd) {
    return megapdf_reads_fd(reinterpret_cast<Document*>(handle)->core, static_cast<int>(fd)) == 1 ? JNI_TRUE : JNI_FALSE;
}

// Moves the document onto a private copy of its file before that file is written in place
// (#147). The core's status: 0, or -9 when the copy could not be made.
JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeReadFromCopy(JNIEnv* env, jobject, jlong handle, jbyteArray pathUtf8) {
    jbyte* path = env->GetByteArrayElements(pathUtf8, nullptr);
    const int status = megapdf_read_from_copy(reinterpret_cast<Document*>(handle)->core, reinterpret_cast<const char*>(path));
    env->ReleaseByteArrayElements(pathUtf8, path, JNI_ABORT);
    return static_cast<jint>(status);
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

// The aspect-preserving render clamp (#93/#111): [width, height] for an ideal size.
JNIEXPORT jintArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRenderSize(JNIEnv* env, jobject, jdouble idealWidth,
                                                      jdouble idealHeight) {
    int w = 0, h = 0;
    megapdf_render_size(idealWidth, idealHeight, &w, &h);
    const jint pair[2] = {w, h};
    jintArray out = env->NewIntArray(2);
    if (out != nullptr) env->SetIntArrayRegion(out, 0, 2, pair);
    return out;
}

// Renders into the ARGB_8888 bitmap: the core draws the white ground, the page
// content and the live form-field values in RGBA byte order to match the
// buffer, and refuses (false, never a crash) anything past the render clamp.
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
    const int status = megapdf_render(p->core, pixels, static_cast<int>(info.width), static_cast<int>(info.height),
                                      static_cast<int>(info.stride), MEGAPDF_RENDER_RGBA);
    AndroidBitmap_unlockPixels(env, bitmap);
    return status == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSave(JNIEnv* env, jobject, jlong handle,
                                                jobject outputStream) {
    auto* d = reinterpret_cast<Document*>(handle);
    jclass streamClass = env->GetObjectClass(outputStream);
    jmethodID write = env->GetMethodID(streamClass, "write", "([BII)V");
    if (write == nullptr) return JNI_FALSE;

    // The core commits any in-progress form edit and does the full rewrite (#97, #110).
    StreamWriter writer{env, outputStream, write, false};
    const int status = megapdf_save(d->core, WriteToStream, &writer);
    return (status == MEGAPDF_OK && !writer.failed) ? JNI_TRUE : JNI_FALSE;
}

// --- Document security (#131). [encrypted, revision, permissions, full access].
JNIEXPORT jintArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSecurityInfo(JNIEnv* env, jobject, jlong handle) {
    megapdf_security s{};
    megapdf_security_info(reinterpret_cast<Document*>(handle)->core, &s);
    const jint values[4] = {s.encrypted, s.revision, static_cast<jint>(s.permissions), s.full_access};
    jintArray out = env->NewIntArray(4);
    if (out != nullptr) env->SetIntArrayRegion(out, 0, 4, values);
    return out;
}

// Passwords arrive as NUL-terminated UTF-8 bytes rather than jstrings: JNI's "modified
// UTF-8" writes characters outside the BMP differently from the UTF-8 a PDF's security
// handler hashes. Returns the core's status; MEGAPDF_ERR_RESTRICTED without full access.
JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSaveWithSecurity(JNIEnv* env, jobject, jlong handle, jobject outputStream,
                                                           jbyteArray userUtf8, jbyteArray ownerUtf8,
                                                           jint permissions) {
    auto* d = reinterpret_cast<Document*>(handle);
    jclass streamClass = env->GetObjectClass(outputStream);
    jmethodID write = env->GetMethodID(streamClass, "write", "([BII)V");
    if (write == nullptr) return MEGAPDF_ERR_ARGUMENT;

    jbyte* user = env->GetByteArrayElements(userUtf8, nullptr);
    jbyte* owner = ownerUtf8 != nullptr ? env->GetByteArrayElements(ownerUtf8, nullptr) : nullptr;
    StreamWriter writer{env, outputStream, write, false};
    int status = megapdf_save_with_security(d->core, reinterpret_cast<const char*>(user),
                                            reinterpret_cast<const char*>(owner),
                                            static_cast<unsigned int>(permissions), WriteToStream, &writer);
    if (owner != nullptr) env->ReleaseByteArrayElements(ownerUtf8, owner, JNI_ABORT);
    env->ReleaseByteArrayElements(userUtf8, user, JNI_ABORT);
    if (status == MEGAPDF_OK && writer.failed) status = MEGAPDF_ERR_PDFIUM;
    return status;
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSaveWithoutSecurity(JNIEnv* env, jobject, jlong handle,
                                                              jobject outputStream) {
    auto* d = reinterpret_cast<Document*>(handle);
    jclass streamClass = env->GetObjectClass(outputStream);
    jmethodID write = env->GetMethodID(streamClass, "write", "([BII)V");
    if (write == nullptr) return MEGAPDF_ERR_ARGUMENT;

    StreamWriter writer{env, outputStream, write, false};
    int status = megapdf_save_without_security(d->core, WriteToStream, &writer);
    if (status == MEGAPDF_OK && writer.failed) status = MEGAPDF_ERR_PDFIUM;
    return status;
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
            // PDFium's FPDF_FORMFIELD_RADIOBUTTON (3) and _CHECKBOX (2), which Kotlin decodes.
            packed.push_back(f.kind == MEGAPDF_FIELD_RADIO ? 3.0 : 2.0);
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
    // Indexed by annotation, up to the last MegaPDF stamp: Kotlin's Stamp.annotIndex
    // is a position in this array, and annotations after the last stamp are not ours.
    std::vector<std::vector<jchar>> ids;
    if (megapdf_stamps* stamps = megapdf_stamps_load(p->core)) {
        for (size_t i = 0; i < megapdf_stamp_count(stamps); i++) {
            megapdf_stamp st{};
            if (megapdf_stamp_get(stamps, i, &st) != MEGAPDF_OK || st.annot_index < 0) continue;
            if (static_cast<size_t>(st.annot_index) >= ids.size()) ids.resize(static_cast<size_t>(st.annot_index) + 1);
            const size_t n = megapdf_stamp_id(stamps, i, nullptr, 0);
            std::vector<jchar> id(n);
            if (n > 0) megapdf_stamp_id(stamps, i, id.data(), n);
            ids[static_cast<size_t>(st.annot_index)] = std::move(id);
        }
        megapdf_stamps_free(stamps);
    }
    const int count = static_cast<int>(ids.size());
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray out = env->NewObjectArray(count, stringClass, nullptr);
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
    std::vector<double> packed;
    if (megapdf_stamps* stamps = megapdf_stamps_load(p->core)) {
        for (size_t i = 0; i < megapdf_stamp_count(stamps); i++) {
            megapdf_stamp st{};
            if (megapdf_stamp_get(stamps, i, &st) != MEGAPDF_OK || st.annot_index < 0) continue;
            if (packed.size() < (static_cast<size_t>(st.annot_index) + 1) * 4) packed.resize((static_cast<size_t>(st.annot_index) + 1) * 4, 0.0);
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

// --- The document's own text (#114): lines, the tiered edit and the byte-identical
// --- undo, all in the core (#106, #112, #116, #117). Marshalling only.

extern "C" {

// Per run: [objectIndex, l, b, r, t, fontSize, isTextBox, endsWithSeparator],
// preceded by the run count; then the line count and per line
// [l, b, r, t, runCount, runIndex...]. Crop space. Pairs with nativeTextRunStrings,
// which lists each run's text and font in the same order.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextRunsPacked(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<double> packed;
    if (megapdf_text* t = megapdf_text_load(p->core, MEGAPDF_TEXT_ALL)) {
        const size_t runs = megapdf_text_run_count(t);
        packed.push_back(static_cast<double>(runs));
        for (size_t i = 0; i < runs; i++) {
            megapdf_text_run r{};
            megapdf_text_run_get(t, i, &r);
            const std::vector<jchar> raw = CoreString(t, i, MEGAPDF_TEXT_RUN_TEXT);
            const bool separator = !raw.empty() && (raw.back() == ' ' || raw.back() == '\r' || raw.back() == '\n');
            packed.insert(packed.end(), {static_cast<double>(r.object_index), r.bounds.left, r.bounds.bottom,
                                         r.bounds.right, r.bounds.top, r.font_size, r.is_text_box ? 1.0 : 0.0,
                                         separator ? 1.0 : 0.0});
        }
        const size_t lines = megapdf_text_line_count(t);
        packed.push_back(static_cast<double>(lines));
        for (size_t j = 0; j < lines; j++) {
            megapdf_rect b{};
            megapdf_text_line_get(t, j, &b);
            const size_t n = megapdf_text_line_runs(t, j, nullptr, 0);
            std::vector<size_t> indices(n);
            if (n > 0) megapdf_text_line_runs(t, j, indices.data(), n);
            packed.insert(packed.end(), {b.left, b.bottom, b.right, b.top, static_cast<double>(n)});
            for (size_t k : indices) packed.push_back(static_cast<double>(k));
        }
        megapdf_text_free(t);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

// Each run's text then font, two entries per run, in nativeTextRunsPacked's order.
JNIEXPORT jobjectArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextRunStrings(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<std::vector<jchar>> items;
    if (megapdf_text* t = megapdf_text_load(p->core, MEGAPDF_TEXT_ALL)) {
        for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
            items.push_back(CoreString(t, i, MEGAPDF_TEXT_RUN_TEXT));
            items.push_back(CoreString(t, i, MEGAPDF_TEXT_RUN_FONT));
        }
        megapdf_text_free(t);
    }
    return StringArray(env, items);
}

// [status, outcome, originalHandle]: status 0 is success; outcome 0 means the run's own
// font drew the text, 1 a standard face; the handle is the untouched original (#117).
JNIEXPORT jlongArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSetText(JNIEnv* env, jobject, jlong handle, jint objectIndex,
                                                  jstring text) {
    auto* p = reinterpret_cast<Page*>(handle);
    std::vector<jchar> wide = JavaChars(env, text);
    wide.push_back(0);
    int outcome = -1;
    megapdf_detached* original = nullptr;
    const int status = megapdf_set_text(p->core, objectIndex, wide.data(), 0, &outcome, &original);
    const jlong values[3] = {status, outcome, reinterpret_cast<jlong>(original)};
    jlongArray out = env->NewLongArray(3);
    if (out != nullptr) env->SetLongArrayRegion(out, 0, 3, values);
    return out;
}

JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeDetachObject(JNIEnv*, jobject, jlong handle, jint objectIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    return reinterpret_cast<jlong>(megapdf_detach_object(p->core, objectIndex));
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRestoreObject(JNIEnv*, jobject, jlong handle, jlong detached,
                                                        jint objectIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    return megapdf_restore_object(p->core, reinterpret_cast<megapdf_detached*>(detached), objectIndex) == MEGAPDF_OK
               ? JNI_TRUE : JNI_FALSE;
}

// Undoes nativeSetText: the core takes the edited run off and puts the original back where it
// was, with any hidden copy of the run the edit took along (#136).
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRestoreOriginal(JNIEnv*, jobject, jlong handle, jlong original,
                                                          jint objectIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    if (megapdf_object_type(p->core, objectIndex) != 1) return JNI_FALSE;   // FPDF_PAGEOBJ_TEXT: the edited run
    return megapdf_restore_detached(p->core, reinterpret_cast<megapdf_detached*>(original)) == MEGAPDF_OK
               ? JNI_TRUE : JNI_FALSE;
}

static std::vector<int> JavaInts(JNIEnv* env, jintArray array) {
    std::vector<int> out;
    if (array == nullptr) return out;
    const jsize n = env->GetArrayLength(array);
    out.resize(static_cast<size_t>(n));
    if (n > 0) env->GetIntArrayRegion(array, 0, n, reinterpret_cast<jint*>(out.data()));
    return out;
}

// As nativeSetText for a whole line (#136): the text into the first index's run, the other runs
// and every run's hidden copies off the page, in one core call. [status, outcome, originalsHandle].
JNIEXPORT jlongArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeSetLineText(JNIEnv* env, jobject, jlong handle, jintArray objectIndices,
                                                      jstring text) {
    auto* p = reinterpret_cast<Page*>(handle);
    const std::vector<int> indices = JavaInts(env, objectIndices);
    std::vector<jchar> wide = JavaChars(env, text);
    wide.push_back(0);
    int outcome = -1;
    megapdf_detached* originals = nullptr;
    const int status = megapdf_set_line_text(p->core, indices.data(), indices.size(), wide.data(), 0, &outcome, &originals);
    const jlong values[3] = {status, outcome, reinterpret_cast<jlong>(originals)};
    jlongArray out = env->NewLongArray(3);
    if (out != nullptr) env->SetLongArrayRegion(out, 0, 3, values);
    return out;
}

// A line's runs and the hidden copies drawn under them (#136), off the page in one handle; 0 on refusal.
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeDetachTextRuns(JNIEnv* env, jobject, jlong handle, jintArray objectIndices) {
    auto* p = reinterpret_cast<Page*>(handle);
    const std::vector<int> indices = JavaInts(env, objectIndices);
    return reinterpret_cast<jlong>(megapdf_detach_text_runs(p->core, indices.data(), indices.size()));
}

// Undoes nativeSetLineText or nativeDetachTextRuns: every object back at its own index.
JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRestoreDetached(JNIEnv*, jobject, jlong handle, jlong detached) {
    auto* p = reinterpret_cast<Page*>(handle);
    return megapdf_restore_detached(p->core, reinterpret_cast<megapdf_detached*>(detached)) == MEGAPDF_OK
               ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeDiscardDetached(JNIEnv*, jobject, jlong detached) {
    megapdf_discard_detached(reinterpret_cast<megapdf_detached*>(detached));
}

}  // extern "C"

extern "C" {

// 1 when the run at objectIndex can be changed without PDFium disturbing the rest of
// the page when it rewrites the content stream, 0 when not (#118).
JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextEditable(JNIEnv*, jobject, jlong handle, jint objectIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    return megapdf_text_editable(p->core, objectIndex);
}

// [status, editable, cause, where, changedPixels, totalPixels, maxShiftPt] (#128).
static jdoubleArray PackLayoutVerdict(JNIEnv* env, int status, const megapdf_layout_verdict& v) {
    const jdouble values[7] = {static_cast<jdouble>(status), static_cast<jdouble>(v.editable), static_cast<jdouble>(v.cause),
                               static_cast<jdouble>(v.where), static_cast<jdouble>(v.changed_pixels),
                               static_cast<jdouble>(v.total_pixels), v.max_shift_pt};
    jdoubleArray out = env->NewDoubleArray(7);
    if (out != nullptr) env->SetDoubleArrayRegion(out, 0, 7, values);
    return out;
}

// nativeTextEditable with its reason: the same cached dry run. Status 1, 0 or MEGAPDF_ERR_ARGUMENT.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeTextEditableReason(JNIEnv* env, jobject, jlong handle, jint objectIndex) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_layout_verdict v{};
    const int status = megapdf_text_editable_reason(p->core, objectIndex, &v);
    return PackLayoutVerdict(env, status, v);
}

// Whether regenerating the page changes how it looks (#139). Status 1, 0 or MEGAPDF_ERR_ARGUMENT.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageRegenerationVerdict(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_layout_verdict v{};
    const int status = megapdf_page_regeneration_verdict(p->core, &v);
    return PackLayoutVerdict(env, status, v);
}

// A cancel flag for a page check started early (#145). Raise may come from any thread.
JNIEXPORT jlong JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeCancelNew(JNIEnv*, jobject) {
    return reinterpret_cast<jlong>(megapdf_cancel_new());
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeCancelRaise(JNIEnv*, jobject, jlong cancel) {
    megapdf_cancel_raise(reinterpret_cast<megapdf_cancel*>(cancel));
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeCancelFree(JNIEnv*, jobject, jlong cancel) {
    megapdf_cancel_free(reinterpret_cast<megapdf_cancel*>(cancel));
}

// The page check run off the engine thread (#145): the core lets go of its lock between the
// dry run's stages. Status 1, 0, MEGAPDF_ERR_CANCELLED or MEGAPDF_ERR_ARGUMENT. The Kotlin side
// keeps the document open until this returns.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageRegenerationVerdictCancellable(JNIEnv* env, jobject, jlong handle,
                                                                             jlong cancel) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_layout_verdict v{};
    const int status =
        megapdf_page_regeneration_verdict_cancellable(p->core, reinterpret_cast<const megapdf_cancel*>(cancel), &v);
    return PackLayoutVerdict(env, status, v);
}

// The cached page verdict without running anything (#145). Status 1, 0, MEGAPDF_ERR_NOT_JUDGED
// or MEGAPDF_ERR_ARGUMENT.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativePageRegenerationVerdictCached(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_layout_verdict v{};
    const int status = megapdf_page_regeneration_verdict_cached(p->core, &v);
    return PackLayoutVerdict(env, status, v);
}

// The verdict behind this thread's latest layout refusal: call straight after the refused call.
JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeLastLayoutVerdict(JNIEnv* env, jobject) {
    megapdf_layout_verdict v{};
    const int status = megapdf_last_layout_verdict(&v);
    return PackLayoutVerdict(env, status, v);
}

// --- Contract 8: redaction (#173) ---
//
// Marks are the core's own and are never written to the file, so the app draws them and
// nothing here touches the page. Rectangles go over packed, as everywhere else on this
// boundary: [id, l, b, r, t] per mark, PDF points, bottom-left origin.

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeMarkForRedaction(JNIEnv*, jobject, jlong handle, jdouble left,
                                                            jdouble bottom, jdouble right, jdouble top) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_rect area{left, bottom, right, top};
    int id = -1;
    return megapdf_redaction_mark(p->core, &area, &id) == MEGAPDF_OK ? id : -1;
}

// Marks the text a selection covers, one mark per line, each grown to whole glyphs.
// Returns how many were made; 0 when the selection covers no text, and the caller then
// marks the rectangle itself. NOT count-then-fill: this call MAKES the marks.
JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeMarkTextForRedaction(JNIEnv*, jobject, jlong handle, jdouble left,
                                                                jdouble bottom, jdouble right, jdouble top) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_rect selection{left, bottom, right, top};
    return static_cast<jint>(megapdf_redaction_mark_text(p->core, &selection, nullptr, 0));
}

JNIEXPORT jdoubleArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRedactionMarksPacked(JNIEnv* env, jobject, jlong handle) {
    auto* p = reinterpret_cast<Page*>(handle);
    const size_t count = megapdf_redaction_marks(p->core, nullptr, 0);
    std::vector<megapdf_redaction_area> areas(count);
    if (count > 0) megapdf_redaction_marks(p->core, areas.data(), count);
    std::vector<double> packed;
    packed.reserve(count * 5);
    for (const megapdf_redaction_area& a : areas) {
        packed.push_back(a.mark_id);
        packed.push_back(a.bounds.left);
        packed.push_back(a.bounds.bottom);
        packed.push_back(a.bounds.right);
        packed.push_back(a.bounds.top);
    }
    jdoubleArray out = env->NewDoubleArray(static_cast<jsize>(packed.size()));
    if (out != nullptr && !packed.empty()) {
        env->SetDoubleArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeMoveRedactionMark(JNIEnv*, jobject, jlong handle, jint markId,
                                                             jdouble left, jdouble bottom, jdouble right,
                                                             jdouble top) {
    auto* p = reinterpret_cast<Page*>(handle);
    megapdf_rect area{left, bottom, right, top};
    return megapdf_redaction_move_mark(p->core, markId, &area) == MEGAPDF_OK ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRemoveRedactionMark(JNIEnv*, jobject, jlong handle, jint markId) {
    megapdf_redaction_remove_mark(reinterpret_cast<Page*>(handle)->core, markId);
}

JNIEXPORT jint JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRedactionMarkCount(JNIEnv*, jobject, jlong handle) {
    return static_cast<jint>(megapdf_redaction_mark_count(reinterpret_cast<Document*>(handle)->core));
}

JNIEXPORT void JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeClearRedactionMarks(JNIEnv*, jobject, jlong handle) {
    megapdf_redaction_clear(reinterpret_cast<Document*>(handle)->core);
}

// Applies every mark. The result goes back packed, so one call carries the whole report:
//   [status, areas, pages, characters, textRuns, partialRuns, hiddenCopies, images,
//    inlineImages, softMasks, paths, shadings, formXObjects, annotations, formFields,
//    links, outlineEntries, structureEntries, pageLabels, metadataFields,
//    refusalCount, (pageIndex, reason) per refusal...]
// status is MEGAPDF_OK, or the core's error; a refusal removed NOTHING.
JNIEXPORT jintArray JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeApplyRedactions(JNIEnv* env, jobject, jlong handle) {
    auto* d = reinterpret_cast<Document*>(handle);
    megapdf_redaction_report* report = nullptr;
    const int status = megapdf_redact_apply(d->core, nullptr, &report);
    megapdf_redaction_counts c{};
    std::vector<jint> packed;
    packed.push_back(status);
    if (report != nullptr) megapdf_redaction_report_counts(report, &c);
    const jint values[19] = {c.areas, c.pages, c.characters, c.text_runs, c.partial_runs, c.hidden_copies,
                             c.images, c.inline_images, c.soft_masks, c.paths, c.shadings, c.form_xobjects,
                             c.annotations, c.form_fields, c.links, c.outline_entries, c.structure_entries,
                             c.page_labels, c.metadata_fields};
    for (jint v : values) packed.push_back(v);
    size_t refusals = 0;
    if (report != nullptr) {
        refusals = megapdf_redaction_refusals(report, nullptr, 0);
        std::vector<megapdf_redaction_refusal> list(refusals);
        if (refusals > 0) megapdf_redaction_refusals(report, list.data(), refusals);
        packed.push_back(static_cast<jint>(refusals));
        for (const megapdf_redaction_refusal& r : list) {
            packed.push_back(r.page_index);
            packed.push_back(r.reason);
        }
        megapdf_redaction_report_free(report);
    } else {
        packed.push_back(0);
    }
    jintArray out = env->NewIntArray(static_cast<jsize>(packed.size()));
    if (out != nullptr && !packed.empty()) {
        env->SetIntArrayRegion(out, 0, static_cast<jsize>(packed.size()), packed.data());
    }
    return out;
}

JNIEXPORT jboolean JNICALL
Java_com_megapdf_engine_PdfiumNative_nativeRedactionPoisoned(JNIEnv*, jobject, jlong handle) {
    return megapdf_redaction_poisoned(reinterpret_cast<Document*>(handle)->core) == 1 ? JNI_TRUE : JNI_FALSE;
}

}  // extern "C"
