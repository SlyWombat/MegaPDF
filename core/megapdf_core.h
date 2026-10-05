// MegaPDF shared engine core — the C ABI (ADR-003, #33).
//
// One implementation of the engine policy — everything between a platform's UI
// and PDFium's C API — bound from C# (P/Invoke), Kotlin (JNI) and Swift (C
// interop). Native UI stays native; this is the layer that used to be written
// three times and fixed three times (#30).
//
// ABI rules, so three toolchains can agree:
//   * C only. No C++ types cross the boundary, no exceptions escape.
//   * The core owns documents (#105, ADR-003 decision 1): a binding hands over
//     bytes and gets opaque handles back. The form-fill environment, the page
//     lifecycle and the serialising mutex all live in here.
//   * Every entry point either returns a count or a status (0 ok, negative
//     error); megapdf_last_error_message() explains the most recent failure on
//     the calling thread. Nothing throws, nothing crashes on a PDFium refusal.
//   * Buffers are caller-owned, count-then-fill: a call with capacity 0 returns
//     how much is needed, a call with a buffer fills up to its capacity and
//     returns the total. That keeps JNI and P/Invoke marshalling boring.
//   * Coordinates are crop space — bottom-left origin, the CropBox origin already
//     subtracted, in points: user space units times the page's /UserUnit (#150), turned
//     by the page's /Rotate (#439). One space, for everything: what a render draws, what
//     megapdf_page_width/height measure and every rectangle any contract reports are all
//     in it. Getting that wrong is #30, so it happens once.
//   * Thread safety: every call takes the core's own mutex, because PDFium is not
//     thread-safe and that is a property of the library, not of any platform.
//     Bindings may keep their own discipline on top; correctness does not need it.
//
// Every contract lives here (#105–#112): no binding calls PDFium directly, so
// there are no raw-handle accessors — the opaque handles are the whole surface.
#ifndef MEGAPDF_CORE_H
#define MEGAPDF_CORE_H

#include <stddef.h>

#if defined(_WIN32)
#  if defined(MEGAPDF_CORE_BUILD)
#    define MEGAPDF_API __declspec(dllexport)
#  else
#    define MEGAPDF_API __declspec(dllimport)
#  endif
#else
#  define MEGAPDF_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/** A rectangle in PDF points, bottom-left origin, crop-relative. */
typedef struct megapdf_rect {
    double left;
    double bottom;
    double right;
    double top;
} megapdf_rect;

/** Opaque handles owned by the core. */
typedef struct megapdf_document megapdf_document;
typedef struct megapdf_page megapdf_page;

/** Status codes returned by calls that do not return a count. */
enum {
    MEGAPDF_OK = 0,
    MEGAPDF_ERR_ARGUMENT = -1,    /* a null handle or an out-of-range index */
    MEGAPDF_ERR_PDFIUM = -2,      /* PDFium refused; see megapdf_last_error_message() */
    MEGAPDF_ERR_MEMORY = -3,      /* the core could not allocate */
    MEGAPDF_ERR_NO_FONT = -4,     /* no font could render the text, not even a standard substitute (tier 2 failed) */
    MEGAPDF_ERR_LAYOUT = -5,      /* PDFium would change how the page looks if it rewrote this text (#118) */
    MEGAPDF_ERR_RESTRICTED = -6,  /* the document's security does not allow it; its owner password would (#131) */
    MEGAPDF_ERR_CANCELLED = -7,   /* a page check stopped early: its cancel flag was raised or its document is closing (#145) */
    MEGAPDF_ERR_NOT_JUDGED = -8,  /* megapdf_page_regeneration_verdict_cached(): the page has no answer yet (#145) */
    MEGAPDF_ERR_FILE = -9,        /* a file could not be created, read or written (#147) */
    MEGAPDF_ERR_REDACT = -10,     /* a redaction could not remove everything it had to, so it removed nothing (#173) */
    MEGAPDF_ERR_FIELDS = -11,     /* the pages carry form fields in a /Parent hierarchy this build's PDFium cannot
                                     carry across a page copy (#174), or (once it can, #452) megapdf_pages_import
                                     found a field in such a hierarchy whose top-level name already exists in this
                                     document and cannot be renamed; nothing was changed. Which of the two applies
                                     is a compile-time property of this binary (MEGAPDF_PDFIUM_PATCHES), not
                                     something a caller chooses -- see megapdf_pages_import()/megapdf_pages_extract() */
    MEGAPDF_ERR_CLOSED = -12      /* the document this page, detached object or removed page belonged to has been
                                     closed, so the handle holds nothing; it is still valid, and still has to be
                                     closed or discarded. See "The dead-handle contract" below (#551) */
};

/**
 * The dead-handle contract (#551).
 *
 * megapdf_close() releases what a megapdf_page, megapdf_detached or megapdf_removed_page
 * handle held -- the PDFium page, the detached page objects, the scratch document -- and
 * leaves the handle itself alive and dead: it holds nothing. Such a handle is never freed
 * memory, so there is no ordering a caller has to get right:
 *
 *   - megapdf_close_page(), megapdf_discard_detached() and megapdf_discard_removed_page()
 *     free a dead handle, harmlessly, in any order relative to megapdf_close() -- before it
 *     or after it, on any thread.
 *   - every other call taking one refuses it: MEGAPDF_ERR_CLOSED from an entry point that
 *     returns a status, and 0, 0.0 or NULL from one that returns a count, a size or a
 *     handle, always with megapdf_last_error() == MEGAPDF_LAST_ERR_CLOSED. A render on a
 *     dead page fails; it does not quietly draw nothing.
 *   - the handle must still be closed or discarded. The shell is about 64 bytes and the
 *     core cannot free it, because it cannot know whether the caller still holds it.
 *
 * This replaces the rule that a caller had to close every page handle before its document.
 * That rule was unkeepable: a binding whose engine runs on one thread cannot wait inside
 * megapdf_close() for a page close that is queued behind it (#549, #550). What one binding
 * cannot obey is not a rule. The bindings' own lifetime guards stay as defence in depth.
 *
 * Restoring a detached object or a removed page still consumes its handle, as it always
 * has; using a consumed handle is a different mistake and is still the caller's to avoid.
 *
 * MEGAPDF_LAST_ERR_CLOSED is what megapdf_last_error() carries, in the same space as
 * MEGAPDF_OPEN_ERR_TOO_LARGE: PDFium's own codes are 0-6 and MegaPDF's start at 100, so a
 * binding can tell them apart. It exists because the entry points that answer with a
 * count, a size, a double or NULL have no int to carry MEGAPDF_ERR_CLOSED in, and "0"
 * alone would be exactly the quiet failure this contract is here to prevent.
 */
#define MEGAPDF_LAST_ERR_CLOSED 101u

/**
 * A cancel flag for a page check (#145). megapdf_cancel_raise() may be called from any
 * thread, at any time, also after the check has returned; free the flag once the check has
 * returned. megapdf_cancel_new() returns NULL only when out of memory.
 */
typedef struct megapdf_cancel megapdf_cancel;
MEGAPDF_API megapdf_cancel* megapdf_cancel_new(void);
MEGAPDF_API void megapdf_cancel_raise(megapdf_cancel* cancel);
MEGAPDF_API void megapdf_cancel_free(megapdf_cancel* cancel);

/* --------------------------------------------------------------------------
 * Errors
 * ----------------------------------------------------------------------- */

/**
 * PDFium's FPDF_GetLastError() as of the last failed megapdf_open() on this
 * thread (FPDF_ERR_PASSWORD, FPDF_ERR_FORMAT, ...). 0 when the last open worked.
 * `unsigned int` rather than PDFium's `unsigned long`, which is 4 bytes on Windows
 * and 8 everywhere else — the kind of thing three bindings would each get wrong once.
 */
MEGAPDF_API unsigned int megapdf_last_error(void);

/** A short English message for the most recent failure on this thread; never NULL. */
MEGAPDF_API const char* megapdf_last_error_message(void);

/* --------------------------------------------------------------------------
 * Documents
 * ----------------------------------------------------------------------- */

/**
 * megapdf_last_error() after a failed open. PDFium's own codes are 0–6
 * (FPDF_ERR_*); MegaPDF's start above them so a binding can tell them apart.
 *
 * TOO_LARGE is the file-backed open's only hard size limit, and it bites on
 * Windows alone: FPDF_FILEACCESS describes a file's length and its read offsets
 * as `unsigned long`, which is 32 bits there and 64 bits everywhere else, so a
 * file of 4 GiB or more cannot be addressed through it on Windows (#147).
 */
#define MEGAPDF_OPEN_ERR_TOO_LARGE 100u

/**
 * #665: the bytes are the beginning of a PDF, not something that is not a PDF. PDFium answers
 * FPDF_ERR_FORMAT for both "this is not a PDF" and "this download stopped", which are
 * different problems with different fixes — the second one's fix is to fetch the file again,
 * and "the file is not a valid PDF" never suggests it.
 *
 * Reported in place of FPDF_ERR_FORMAT when a failed open's bytes carry a %PDF- header AND
 * either a /Linearized dictionary whose own /L exceeds the file's length, or no %%EOF within
 * the last 4 KiB. megapdf_last_error_message() then names both sizes, which is what makes it
 * actionable: "1.0 MB present, the PDF's own index says 2.4 MB".
 *
 * A caller that does not care about the distinction can treat it exactly as FPDF_ERR_FORMAT;
 * it is a strictly narrower answer, never returned where that one would not have been.
 */
#define MEGAPDF_OPEN_ERR_INCOMPLETE 102u

/**
 * Opens a document from memory. The bytes are copied; the caller may free them
 * on return. `password_utf8` may be NULL. Returns NULL on failure, with
 * megapdf_last_error() carrying PDFium's code (FPDF_ERR_PASSWORD when one is
 * required or wrong). The form-fill environment is initialised here, so form
 * fields render and toggle without any binding-side setup.
 *
 * This costs the file's size in memory twice over — once in the caller's buffer,
 * once in the copy — so it is for documents that are genuinely in memory (built
 * there, or handed over by a platform that gives no file). A document that has a
 * file should be opened with megapdf_open_file() (#148).
 */
MEGAPDF_API megapdf_document* megapdf_open(const void* bytes, size_t length, const char* password_utf8);

/**
 * Opens a document from its file, read on demand (#147, #148).
 *
 * The core keeps the file open and PDFium reads the parts it needs through it, so
 * opening costs what PDFium's parser caches rather than the size of the file, and
 * a document larger than the address space (or than a .NET byte[]) opens like any
 * other. `path_utf8` is UTF-8 on every platform, including Windows, where the core
 * widens it itself.
 *
 * The file is opened so that it may still be renamed, written or deleted while the
 * document is open, which the atomic-replace save (SDD §3.4) and a cloud sync both
 * need. A save that replaces the file under an open document leaves that document
 * reading the bytes it was opened on, exactly as the in-memory copy did.
 *
 * Returns NULL on failure with megapdf_last_error() set: FPDF_ERR_FILE when the
 * file cannot be opened or read, MEGAPDF_OPEN_ERR_TOO_LARGE past the limit above,
 * and PDFium's own codes otherwise.
 */
MEGAPDF_API megapdf_document* megapdf_open_file(const char* path_utf8, const char* password_utf8);

/**
 * megapdf_open_file() for a file the caller has already opened for reading: the
 * core takes ownership of `fd` and closes it with the document, whether or not the
 * open succeeds. For Android, whose documents arrive as content URIs that have a
 * descriptor but no path. POSIX only — on Windows it fails with FPDF_ERR_FILE.
 */
MEGAPDF_API megapdf_document* megapdf_open_fd(int fd, const char* password_utf8);

/**
 * Opens `bytes` with the credentials `like` was opened with (#132). A save of a
 * protected document writes a copy that is still protected, so reading that copy
 * back needs the same password; every platform's save check opens it this way.
 * The core keeps the password with the document, in memory only, and
 * megapdf_close() wipes it. Returns NULL, with megapdf_last_error() set, when `like`
 * is NULL or the bytes do not open.
 */
MEGAPDF_API megapdf_document* megapdf_open_like(const megapdf_document* like, const void* bytes, size_t length);

/** megapdf_open_file() with the credentials `like` was opened with (#132, #148). */
MEGAPDF_API megapdf_document* megapdf_open_file_like(const megapdf_document* like, const char* path_utf8);

/** megapdf_open_fd() with the credentials `like` was opened with (#132, #148). */
MEGAPDF_API megapdf_document* megapdf_open_fd_like(const megapdf_document* like, int fd);

/**
 * 1 when `document` reads from the same file as `path_utf8` — the same file, not the same
 * name, so a link or a second path to it counts — and 0 otherwise: a document opened from
 * memory, one already moved to a copy, or a path that does not exist (#147).
 *
 * A document opened from its file reads it for as long as it is open. Replacing the file
 * (a write to a sibling, then a rename) is safe; writing over it where it is is not, because
 * the document would then read the new bytes where it expects the old ones. A binding that
 * must write in place — the macOS sandbox, Android's content URIs — asks this first and, when
 * it answers 1, calls megapdf_read_from_copy() before writing.
 */
MEGAPDF_API int megapdf_reads_file(const megapdf_document* document, const char* path_utf8);

/** megapdf_reads_file() for a descriptor, which stays the caller's. POSIX only; 0 on Windows. */
MEGAPDF_API int megapdf_reads_fd(const megapdf_document* document, int fd);

/**
 * Moves `document` onto a private copy of the file it reads, so that file may be written
 * over in place (#147). The core creates `copy_path_utf8` (it must not exist), copies the
 * file into it — a clone on file systems that can, which costs no time or space — and reads
 * from the copy from then on. The copy's name is gone before this returns: nothing is left
 * behind however the app ends, and its space is freed when the document closes.
 *
 * MEGAPDF_OK, with nothing to do for a document opened from memory; MEGAPDF_ERR_FILE when
 * the copy cannot be made (no space, say), and the document still reads the original;
 * MEGAPDF_ERR_ARGUMENT for a NULL document or path. Pages, edits and page checks carry on
 * while the bytes are copied.
 */
MEGAPDF_API int megapdf_read_from_copy(megapdf_document* document, const char* copy_path_utf8);

/**
 * Releases every page still open on it, every detached object and every removed page it is
 * keeping for an undo, tears down the form environment, and frees the document. NULL is fine.
 *
 * Those handles are released, not freed: each stays valid, holds nothing, refuses every call
 * and still has to be closed or discarded — in any order relative to this call. See "The
 * dead-handle contract" above (#551).
 */
MEGAPDF_API void megapdf_close(megapdf_document* document);

MEGAPDF_API int megapdf_page_count(const megapdf_document* document);

/**
 * megapdf_document_flags(). Facts about `document` as a whole, not about a page range —
 * available right after megapdf_open() (unlike contract 9's structure, which needs an
 * explicit load over a range) — MEGAPDF_DOC_* bits, grown the same "frozen struct, grown by
 * new fields/enum values, never new parameters" way contract 9 itself is (#457's design note
 * on structure, :1231): a future fact is a new bit here, never a new parameter or a second
 * call.
 */
enum {
    /**
     * The document is dynamic XFA (#456, #457): PDFium's FPDF_GetFormType() reports
     * FORMTYPE_XFA_FULL — its AcroForm's /XFA entry needs rendering — and the static page
     * content PDFium actually draws, the only content there is because PDFium does not run
     * Acrobat's XFA/JavaScript engine, is Adobe's own stable "please wait... install Adobe
     * Reader" placeholder rather than the real form. Every one of these still opens, reports
     * a plausible page count and draws a page, so nothing *looks* wrong; only filling it is
     * unavailable (that is #458) — view, print, save, share, export and every page tool keep
     * working exactly as before, because none of them depend on the field values XFA would
     * have supplied.
     *
     * An `/XFA` key alone does NOT set this bit: 78% of a real Canadian federal-forms corpus
     * carries one (#456), and most of those are "hybrid" XFA — FPDF_GetFormType() reports
     * FORMTYPE_XFA_FOREGROUND, meaning the static content *is* the complete, real form and
     * the XFA entry is a foreground layer Acrobat may add but does not need to draw the page
     * (CRA's and Service Canada's fillable forms, and a minority of IRCC's, all extract their
     * real content today and must go on doing so). Nor does an ordinary AcroForm document, or
     * one with no form at all (FORMTYPE_ACRO_FORM / FORMTYPE_NONE).
     *
     * The bit is 1 only when both hold: FPDF_GetFormType() answers FORMTYPE_XFA_FULL, and one
     * of Adobe's two known placeholder templates appears verbatim in the first pages' own
     * text. Measured against #456's 134-document Canadian corpus (105 with `/XFA`): every one
     * of the 40 real dynamic-XFA forms matches on both counts, all 65 hybrid-XFA forms match
     * neither (T4 and ISP-1000 spot-checked as extracting their real content) — an exact
     * split, not a heuristic guess.
     */
    MEGAPDF_DOC_DYNAMIC_XFA = 1u << 0,

    /**
     * The document carries an existing digital signature (#476, #481 phase 1): PDFium's
     * FPDF_GetSignatureCount() is greater than zero. This is a fact about the document as
     * opened, not a verdict on the signature's cryptographic validity — the core does not
     * verify one, the same way it does not verify a password beyond what open already
     * requires.
     *
     * It exists because `megapdf_save()` cannot preserve a signature: it calls PDFium's
     * FPDF_SaveAsCopy, which re-serialises the whole file, so any `/ByteRange` a signature
     * recorded no longer covers the saved file. Measured in #476 against 33 genuinely
     * signed GPO documents (verified with poppler's `pdfsig`, independent of MegaPDF):
     * every one goes from "Signature is Valid" to "Digest Mismatch" after
     * `megapdf_save()`, whether or not anything was actually edited. Nothing refuses the
     * save — this flag exists so a caller can warn before it happens, at the point of
     * save, not as a banner on open (the common case, saving a copy, is unaffected: the
     * signed original is untouched). See MEGAPDF_DOC_SIGNED_CERTIFICATION for the one case
     * where the wording should differ.
     */
    MEGAPDF_DOC_SIGNED = 1u << 1,

    /**
     * At least one of the document's signatures is a certification signature carrying a
     * `/DocMDP` transform (FPDFSignatureObj_GetDocMDPPermission() succeeds, answering 1, 2
     * or 3) rather than an ordinary approval signature. Always accompanied by
     * MEGAPDF_DOC_SIGNED; the two are separate bits because the wording differs, not
     * because either implies the document is otherwise safe to overwrite.
     *
     * A certification signature can *forbid* modification outright (DocMDP permission 1,
     * "no changes"), rather than merely being invalidated by one the way an ordinary
     * signature is — the document may say, in its own structure, that MegaPDF's save was
     * never allowed to happen. Measured across the same 33-document #476 corpus (all
     * `govinfo-signed`, staged read-only at ~/pdf-public on kdocker3): 33 of 33 are
     * certification signatures, and every one is permission 1 (no changes allowed at
     * all) — GPO certifies its publications closed to any modification, not merely to
     * form-filling (permission 2) or annotation (permission 3). This split was
     * unmeasured going into #481; a corpus of one signer's homogeneous population, so a
     * more varied sample could change the proportions without changing the qualitative
     * finding that DocMDP is cheap to read and worth a different warning.
     */
    MEGAPDF_DOC_SIGNED_CERTIFICATION = 1u << 2
};

/**
 * MEGAPDF_DOC_* bits describing `document` as a whole. 0 for a NULL document or an ordinary
 * one. Cheap — a form-type check (and, only when that says XFA_FULL, a substring search over
 * the first few pages' text), and a PDFium signature-table read (and, only for a document
 * that has a signature, one DocMDP-permission call per signature) — so it is safe to call
 * right after megapdf_open() and as often as wanted.
 */
MEGAPDF_API unsigned int megapdf_document_flags(const megapdf_document* document);

/* --------------------------------------------------------------------------
 * Digital signatures (#576). MEGAPDF_DOC_SIGNED says a document carries one;
 * these say what little can honestly be said about it, and remove it on request.
 *
 * Why an app needs more than the bit. A save invalidates the signature (#476,
 * measured 33/33), so the app asks whether to remove it rather than deciding for
 * the person — and a question about "the signature" has to name which signature,
 * or the person cannot tell what they are being asked to throw away.
 *
 * What can be named, and what cannot. PDFium hands us the byte range, the raw
 * contents, the certification permission, the *reason* and the *signing time*. It
 * does NOT hand us the signer's name: that lives in the certificate's subject,
 * inside the PKCS#7 blob, and reading it would mean ASN.1 and X.509 parsing in
 * shared code that five platforms link. #476's study did exactly that parsing, in
 * Python with a library, for a one-off measurement; doing it here is a different
 * proposition. So this reports the date and the reason, which cost one dictionary
 * read each, and the signer's name is a later improvement. The choice being
 * offered — remove it or keep it — does not depend on knowing the name.
 * ----------------------------------------------------------------------- */

/**
 * One of the document's digital signatures. Facts about the document as opened, not a
 * verdict on the signature's cryptographic validity: the core verifies nothing (see
 * MEGAPDF_DOC_SIGNED), and #476 settled that a signature cannot survive our save at all.
 */
typedef struct megapdf_signature {
    /** 1, 2 or 3 for a certification (`/DocMDP`) signature; 0 for an ordinary approval one. */
    int docmdp_permission;
    /** The `/M` signing time, 0 in every field when the signature records none or records
     *  one the core cannot read. Deliberately components rather than a formatted string:
     *  a date shown to a person is formatted in their language, which is the app's job and
     *  not the engine's, and parsing `D:YYYYMMDDHHMMSS` once here beats doing it in each of
     *  the five apps. `utc_offset_minutes` is the signer's stated offset from UTC; the other
     *  fields are the signer's own local time, unconverted, which is what a signature says. */
    int year;                 /* 0, or 1-9999 */
    int month;                /* 0, or 1-12 */
    int day;                  /* 0, or 1-31 */
    int hour;                 /* 0-23 */
    int minute;               /* 0-59 */
    int second;               /* 0-59 */
    int utc_offset_minutes;   /* signed; 0 for Z, for no offset recorded, or for UTC itself */
    /** Non-zero when the signature records a `/Reason`, so an app can ask for it without
     *  a count-then-fill round trip that answers 0. */
    int has_reason;
} megapdf_signature;

/**
 * How many digital signatures `document` carries — the same population
 * MEGAPDF_DOC_SIGNED is set from, so 0 exactly when that bit is clear. -1 for a NULL
 * document.
 *
 * A signature *field* with no signature in it does not count, and this is the fix for
 * a measured false positive (#476 §5b, #576): PDFium's FPDF_GetSignatureCount() counts
 * every `/FT /Sig` entry in the AcroForm's field tree whether or not it holds a
 * signature, so an unsigned signature field — a document prepared for signing and not
 * yet signed — read as signed, and the app warned that saving would invalidate a
 * signature that does not exist. Three documents in the 5,636-document corpus do this,
 * all synthetic; none of the 4,337 private documents and none of the 33 genuinely
 * signed government ones. The test is whether the field's `/V` carries signature data
 * at all (a `/ByteRange` or `/Contents`), which is the same thing #476 measured against
 * the decompressed bytes.
 */
MEGAPDF_API int megapdf_signature_count(const megapdf_document* document);

/**
 * The signature at `index` (0 to megapdf_signature_count()-1 — the counted population,
 * so indices skip any valueless signature field). MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT
 * for a NULL document, a NULL `out` or an index out of range.
 */
MEGAPDF_API int megapdf_signature_info(const megapdf_document* document, int index, megapdf_signature* out);

/**
 * The signature's `/Reason` — the signer's own words about why they signed, which is
 * frequently the most identifying thing a document says about its signature. UTF-16 code
 * units, no terminator, count-then-fill; 0 when there is no reason, for a bad index, or
 * for a NULL document.
 */
MEGAPDF_API size_t megapdf_signature_reason(const megapdf_document* document, int index,
                                            unsigned short* out, size_t capacity);

/**
 * Removes every digital signature from the open document, in memory: each signature
 * field leaves the AcroForm's field tree with its value, and its widget leaves the page
 * it was on. Returns the number removed, or -1 for a NULL document.
 *
 * It is not a repair and it does not pretend to be one. #476 measured that our save
 * cannot preserve a signature — 33 of 33 real signed documents go from valid to digest
 * mismatch with no edit at all — and #476 closed the question of preserving one as an
 * accepted, permanent limitation. What this call is for is the other half: a save
 * otherwise carries the dead signature dictionary into the output, where
 * FPDF_GetSignatureCount() still counts it (98 of 98, #476 §5a), so the saved file
 * presents as signed and the app warns, on the next open, about a signature it has
 * already destroyed. Remove it and the saved file honestly reports what it is.
 *
 * Whose decision it is. Removing something the author put there is not the core's call
 * and not the app's: the person looking at the document decides, and the apps ask
 * (#481's warning gained the choice; #576). The core only does as it is told. Nothing
 * here is automatic — megapdf_save() does not call this, and a save with the signature
 * kept behaves exactly as it did before.
 *
 * It asks no permission bit, deliberately. The advisory bits are the subject of #558's
 * rule — explain what the author asked, then let the person decide — and this call *is*
 * that decision, arrived at through a question the app has already put and the person has
 * already answered. Gating it would mean asking the same person the same thing twice, or
 * putting back the wall #558 took down. A certification signature's own `/DocMDP` policy
 * is not a gate either: a policy declaring the document closed to changes is a statement
 * about the signed revision, and #476 settled that our save ends that revision whatever
 * we do.
 *
 * Afterwards megapdf_signature_count() answers 0, megapdf_document_flags() reports
 * neither MEGAPDF_DOC_SIGNED nor MEGAPDF_DOC_SIGNED_CERTIFICATION, and a save writes a
 * file that opens the same way. In memory only: the file on disk is untouched until
 * something saves, and nothing is recorded anywhere.
 *
 * One honest caveat, which is why this returns a count rather than a status. A
 * signature field is reached through the widget annotation that draws it, and a
 * signature with no widget on any page would be out of reach. Every one of #476's 33
 * genuinely signed documents has its widget on a page (measured for #576, 33 of 33 —
 * the field dictionary *is* the annotation), as do both fixtures, so this is a
 * possibility rather than an observation; a caller that must be sure should ask
 * megapdf_signature_count() afterwards rather than trust the count returned.
 */
MEGAPDF_API int megapdf_signatures_remove(megapdf_document* document);

/* --------------------------------------------------------------------------
 * Pages
 * ----------------------------------------------------------------------- */

/**
 * Loads page `index` (form-fill hooks applied). Returns NULL on failure. A page must be
 * closed with megapdf_close_page().
 *
 * Closing the document releases every page still open on it — the PDFium page goes, and the
 * page handle stays valid and holds nothing. Every call on it then fails
 * (MEGAPDF_ERR_CLOSED, or megapdf_last_error() == MEGAPDF_LAST_ERR_CLOSED for the calls with
 * no status to return), and megapdf_close_page() frees it, in any order relative to
 * megapdf_close(). See "The dead-handle contract" above (#551).
 */
MEGAPDF_API megapdf_page* megapdf_load_page(megapdf_document* document, int index);
MEGAPDF_API void megapdf_close_page(megapdf_page* page);

/**
 * Page size in points — the CropBox size, which is what a viewer shows, times the page's
 * /UserUnit, and rotated: a quarter-turned page answers with its width and height swapped,
 * which is the size crop space and the render both use (#439).
 */
MEGAPDF_API double megapdf_page_width(const megapdf_page* page);
MEGAPDF_API double megapdf_page_height(const megapdf_page* page);

/**
 * The CropBox origin in PDF user space that every returned coordinate has had subtracted.
 * The *unrotated* term of the transform: crop space also turns with the page's /Rotate
 * (#439), and a rect the core reports has both applied already.
 */
MEGAPDF_API void megapdf_page_crop_origin(const megapdf_page* page, double* out_x, double* out_y);
/**
 * The page's /UserUnit (#150): how many points one user space unit is, 1.0 for almost every
 * page. Crop space is already scaled by it: page sizes, bounds, font sizes and every
 * coordinate the core returns or takes are points, so a 10 m banner drawn in 2-point units
 * measures 10 m. 1.0 for a NULL page. Needs MegaPDF's PDFium patch 0023.
 */
MEGAPDF_API double megapdf_page_user_unit(const megapdf_page* page);

/* --------------------------------------------------------------------------
 * Contracts (SDD §6.2)
 * ----------------------------------------------------------------------- */

/**
 * Drawn-checkbox candidates (contract 2): stroked-not-filled path objects, 6–24 pt
 * on both axes, squareness within 25%. Count-then-fill into `out`.
 */
MEGAPDF_API size_t megapdf_detect_checkbox_squares(const megapdf_page* page, megapdf_rect* out, size_t capacity);

/**
 * Text search (#26): case-insensitive literal substring, matches in text order,
 * each match covered by one rect per line it spans. Count-then-fill into `out`
 * as a packed double stream — for each match: rect_count, then rect_count × (left,
 * bottom, right, top) — the layout every binding already decodes. The return value
 * is the total number of doubles; a NULL or empty term yields 0. Matches with no
 * rects are dropped, as on every platform today.
 *
 * `term_utf16` is NUL-terminated UTF-16 (what PDFium's FPDF_WIDESTRING is).
 */
MEGAPDF_API size_t megapdf_search_page(const megapdf_page* page, const unsigned short* term_utf16,
                                       double* out, size_t capacity);

/* --------------------------------------------------------------------------
 * Contract 2: text runs and visual lines (#106) — the inputs to body-text
 * editing. A page's runs and lines are computed once into an opaque result that
 * outlives the page; strings come out count-then-fill as UTF-16 code units.
 * ----------------------------------------------------------------------- */

/** One text object with visible text: a run of body text in one font and size. */
typedef struct megapdf_text_run {
    int object_index;        /* index into the page's object list */
    megapdf_rect bounds;     /* crop space */
    double font_size;
    int is_text_box;         /* 1 when the object carries the MegaPDFTextBox mark (SDD §6.2 contract 4) */
} megapdf_text_run;

/** Which string of a run megapdf_text_run_string() returns. */
typedef enum megapdf_text_field {
    MEGAPDF_TEXT_RUN_TEXT = 0,      /* the glyphs, as FPDFTextObj_GetText reports them */
    MEGAPDF_TEXT_RUN_FONT = 1,      /* the font's family name ("" when the font is unreadable) */
    MEGAPDF_TEXT_RUN_BOX_ID = 2,    /* the `id` mark param, "" when none */
    MEGAPDF_TEXT_RUN_BOX_FONT = 3   /* the `font` mark param, "" when none (the binding applies its default) */
} megapdf_text_field;

typedef struct megapdf_text megapdf_text;

/** Flags for megapdf_text_load(). */
enum {
    MEGAPDF_TEXT_ALL = 0,
    /** Only objects carrying the MegaPDFTextBox mark — what a text-box listing needs,
        without reading every body-text object on the page. Lines are not meaningful
        for such a load. */
    MEGAPDF_TEXT_BOXES_ONLY = 1
};

/**
 * Reads every text object on the page in object order, skipping objects whose
 * text is empty or all whitespace. Visual lines are computed on first use: runs
 * whose vertical centres are within half the taller run's height share a
 * baseline; a baseline is split where the horizontal gap to the next run (left
 * to right) exceeds twice the larger of the two font sizes (columns, page-number
 * gutters). Lines are ordered top to bottom, then left to right. Returns NULL
 * only when the page is NULL or the core cannot allocate.
 */
MEGAPDF_API megapdf_text* megapdf_text_load(const megapdf_page* page, unsigned int flags);
MEGAPDF_API void megapdf_text_free(megapdf_text* text);

MEGAPDF_API size_t megapdf_text_run_count(const megapdf_text* text);
/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a bad handle or index. */
MEGAPDF_API int megapdf_text_run_get(const megapdf_text* text, size_t index, megapdf_text_run* out);
/** UTF-16 code units, no terminator, count-then-fill. 0 for a bad handle, index or field. */
MEGAPDF_API size_t megapdf_text_run_string(const megapdf_text* text, size_t index, megapdf_text_field field,
                                           unsigned short* out, size_t capacity);

MEGAPDF_API size_t megapdf_text_line_count(const megapdf_text* text);
/** The line's bounds (the union of its runs) in crop space. */
MEGAPDF_API int megapdf_text_line_get(const megapdf_text* text, size_t index, megapdf_rect* out_bounds);
/** The run indices making up the line, left to right; count-then-fill. */
MEGAPDF_API size_t megapdf_text_line_runs(const megapdf_text* text, size_t index, size_t* out_run_indices,
                                          size_t capacity);

/* --------------------------------------------------------------------------
 * Contract 3: AcroForm fields (#107). The form-fill environment lives in the
 * core, so reading fields and driving them through PDFium's form machinery
 * (a simulated click, which keeps /V, /AS and radio-group siblings consistent,
 * exactly as the Chrome viewer does) happens here for every platform.
 * ----------------------------------------------------------------------- */

typedef enum megapdf_field_kind {
    MEGAPDF_FIELD_OTHER = 0,
    MEGAPDF_FIELD_TEXT = 1,
    MEGAPDF_FIELD_CHECKBOX = 2,
    MEGAPDF_FIELD_RADIO = 3
} megapdf_field_kind;

typedef struct megapdf_form_field {
    int kind;                /* megapdf_field_kind */
    int is_checked;          /* checkbox and radio only; 0 otherwise */
    megapdf_rect bounds;     /* crop space */
} megapdf_form_field;

typedef enum megapdf_field_string {
    MEGAPDF_FIELD_NAME = 0,  /* the fully qualified field name */
    MEGAPDF_FIELD_VALUE = 1  /* the current value (text fields; the export value of a checked box) */
} megapdf_field_string;

typedef struct megapdf_form_fields megapdf_form_fields;

/**
 * Every widget annotation on the page, in annotation order, with its kind, state
 * and bounds; a snapshot that outlives the page. Returns NULL only when the page
 * is NULL or the core cannot allocate (a document without a form environment
 * yields zero fields).
 */
MEGAPDF_API megapdf_form_fields* megapdf_form_fields_load(const megapdf_page* page);
MEGAPDF_API void megapdf_form_fields_free(megapdf_form_fields* fields);
MEGAPDF_API size_t megapdf_form_field_count(const megapdf_form_fields* fields);
MEGAPDF_API int megapdf_form_field_get(const megapdf_form_fields* fields, size_t index, megapdf_form_field* out);
/** UTF-16 code units, no terminator, count-then-fill. */
MEGAPDF_API size_t megapdf_form_field_string(const megapdf_form_fields* fields, size_t index,
                                             megapdf_field_string which, unsigned short* out, size_t capacity);

/**
 * A primary-button click at (x, y) in crop space, then focus released: toggles a
 * checkbox or radio under the point through the form environment. Returns
 * MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT when the page is NULL or the document has
 * no form environment.
 */
MEGAPDF_API int megapdf_form_click(const megapdf_page* page, double x, double y);

/**
 * Sets the text of the field under (x, y) in crop space: click to focus, select
 * all, replace the selection with `value_utf16` (NUL-terminated), release focus.
 */
MEGAPDF_API int megapdf_form_set_text(const megapdf_page* page, double x, double y,
                                      const unsigned short* value_utf16);

/**
 * Commits any in-progress field edit (FORM_ForceToKillFocus). Call before
 * serialising; the rule every platform had to remember on its own until now.
 */
MEGAPDF_API void megapdf_form_commit(const megapdf_document* document);

/* --------------------------------------------------------------------------
 * Contract 4: stamps and MegaPDF_Id marks (#108, SDD §6.2). A MegaPDF stamp is
 * a STAMP annotation carrying a `MegaPDF_Id` string ("mark:…" for check marks,
 * "sig:…" for signatures). Ids are caller-supplied so they stay stable across
 * undo/redo and across platforms. Coordinates are crop space throughout.
 * ----------------------------------------------------------------------- */

typedef enum megapdf_mark_style {
    MEGAPDF_MARK_CROSS = 0,          /* ✗ — the default (Appendix B #3) */
    MEGAPDF_MARK_CHECK = 1,          /* ✓ */
    MEGAPDF_MARK_FILLED_SQUARE = 2   /* ■ */
} megapdf_mark_style;

/**
 * Draws a check mark over a drawn square: STAMP annot inset 10% of the larger
 * side, 0x202020 ink, stroke width max(1.2, width × 0.11), tagged with `id_utf16`
 * (NUL-terminated). MEGAPDF_OK, MEGAPDF_ERR_ARGUMENT, or MEGAPDF_ERR_PDFIUM.
 */
MEGAPDF_API int megapdf_add_check_mark(const megapdf_page* page, const megapdf_rect* square,
                                       megapdf_mark_style style, const unsigned short* id_utf16);

/**
 * Places an image stamp (a signature): STAMP annot at `bounds`, the image object
 * positioned by the unit-square matrix, alpha respected. `bgra` is width × height
 * × 4 bytes, top row first; the core copies it.
 */
MEGAPDF_API int megapdf_add_image_stamp(const megapdf_page* page, const unsigned char* bgra, int width, int height,
                                        const megapdf_rect* bounds, const unsigned short* id_utf16);
/* On a rotated page the image is turned with the page and fills `bounds` as given, not a box
 * whose aspect the turn has swapped (#446, contract 10's Coordinates). */

typedef struct megapdf_stamp {
    int annot_index;         /* the annotation's index on the page, valid until the page's annotations change */
    megapdf_rect bounds;     /* crop space */
} megapdf_stamp;

typedef struct megapdf_stamps megapdf_stamps;

/** Every annotation on the page carrying a MegaPDF_Id, in annotation order; a snapshot. */
MEGAPDF_API megapdf_stamps* megapdf_stamps_load(const megapdf_page* page);
MEGAPDF_API void megapdf_stamps_free(megapdf_stamps* stamps);
MEGAPDF_API size_t megapdf_stamp_count(const megapdf_stamps* stamps);
MEGAPDF_API int megapdf_stamp_get(const megapdf_stamps* stamps, size_t index, megapdf_stamp* out);
/** The stamp's MegaPDF_Id: UTF-16 code units, no terminator, count-then-fill. */
MEGAPDF_API size_t megapdf_stamp_id(const megapdf_stamps* stamps, size_t index, unsigned short* out, size_t capacity);

typedef struct megapdf_image megapdf_image;

/**
 * The image of the stamp at `annot_index`, rendered at the image's NATIVE pixel
 * size rather than its placement size (a temporary 1 pt-per-pixel matrix), so
 * repeated move cycles never lose resolution. NULL when the annotation has no
 * image object.
 */
MEGAPDF_API megapdf_image* megapdf_stamp_image_load(const megapdf_page* page, int annot_index);
MEGAPDF_API void megapdf_image_free(megapdf_image* image);
MEGAPDF_API int megapdf_image_width(const megapdf_image* image);
MEGAPDF_API int megapdf_image_height(const megapdf_image* image);
/** BGRA bytes, top row first, width × 4 per row; count-then-fill in bytes. */
MEGAPDF_API size_t megapdf_image_pixels(const megapdf_image* image, unsigned char* out, size_t capacity);

/** Removes the annotation at `annot_index`. MEGAPDF_OK or MEGAPDF_ERR_PDFIUM. */
MEGAPDF_API int megapdf_remove_annotation(const megapdf_page* page, int annot_index);

/** Removes the stamp carrying `id_utf16`. MEGAPDF_ERR_ARGUMENT when no such stamp. */
MEGAPDF_API int megapdf_remove_stamp(const megapdf_page* page, const unsigned short* id_utf16);

/**
 * Moves an image stamp to `bounds` under the same id: extract at native
 * resolution, remove, re-add. (Updating the annotation in place after SetRect
 * wipes its appearance stream.) MEGAPDF_ERR_ARGUMENT when the id names no image stamp.
 */
MEGAPDF_API int megapdf_move_image_stamp(const megapdf_page* page, const unsigned short* id_utf16,
                                         const megapdf_rect* bounds);

/* --------------------------------------------------------------------------
 * Contract 5: whiteouts, text boxes and detached objects (#109). Whiteouts are
 * white filled paths carrying the MegaPDFWhiteout mark; text boxes are text
 * objects in one of three base-14 faces carrying the MegaPDFTextBox mark with
 * `id` and `font` params (SDD §6.2 contract 4, #43). Coordinates are crop space.
 * ----------------------------------------------------------------------- */

typedef struct megapdf_object_rect {
    int object_index;
    megapdf_rect bounds;
} megapdf_object_rect;

/** Appends a whiteout covering `bounds`; its object index comes back in `out_object_index`. */
MEGAPDF_API int megapdf_add_whiteout(const megapdf_page* page, const megapdf_rect* bounds, int* out_object_index);
/** Every whiteout on the page, in object order; count-then-fill. */
MEGAPDF_API size_t megapdf_whiteouts(const megapdf_page* page, megapdf_object_rect* out, size_t capacity);

/**
 * Inserts a text box at `object_index` (-1 appends) with its baseline starting at
 * (baseline_x, baseline_y). `font_name` must be "Helvetica", "Times-Roman" or
 * "Courier" (MEGAPDF_ERR_ARGUMENT otherwise); `text` and `id` are NUL-terminated
 * UTF-16. The mark records the id and the face exactly as chosen.
 */
MEGAPDF_API int megapdf_add_text_box(const megapdf_page* page, int object_index, const unsigned short* text,
                                     const char* font_name, double font_size, double baseline_x, double baseline_y,
                                     const unsigned short* id, int* out_object_index);
/* The baseline is a line in crop space, so on a rotated page the text reads along what the
 * reader sees as the horizontal, not along user-space x (#446, contract 10's Coordinates). */

/**
 * The id-preserving restyle (#45): inserts a box at `object_index` and then moves
 * it so its bounds' bottom-left corner lands on (left, bottom) — a 12 pt → 18 pt
 * change grows upward from the anchored corner instead of dropping by the extra
 * descender depth. The caller detaches the old object first (megapdf_detach_object).
 */
MEGAPDF_API int megapdf_restyle_text_box(const megapdf_page* page, int object_index, const unsigned short* text,
                                         const char* font_name, double font_size, double left, double bottom,
                                         const unsigned short* id);

/**
 * The object index of the text box carrying `id`, or -1. A marked box with no id
 * (written before the param existed) answers to "text:untagged#<object index>",
 * the derived handle the phones give it. MEGAPDF_ERR_CLOSED for a dead page (#551): a page
 * whose document has closed has no boxes to find, which is not the same as not having this one.
 */
MEGAPDF_API int megapdf_find_text_box(const megapdf_page* page, const unsigned short* id);

/** How many objects the page draws. 0 for a NULL page, MEGAPDF_ERR_CLOSED for a dead one (#551). */
MEGAPDF_API int megapdf_page_object_count(const megapdf_page* page);

/**
 * PDFium's object type (FPDF_PAGEOBJ_TEXT = 1, PATH = 2, IMAGE = 3, ...), or -1 for a bad index.
 * MEGAPDF_ERR_CLOSED for a dead page (#551), which is not the same as "no object there".
 */
MEGAPDF_API int megapdf_object_type(const megapdf_page* page, int object_index);

/** Bounds of any page object, crop space. MEGAPDF_ERR_ARGUMENT for a bad index. */
MEGAPDF_API int megapdf_object_bounds(const megapdf_page* page, int object_index, megapdf_rect* out);

/** Translates a text object so its bounds' bottom-left corner lands on (left, bottom); scale and rotation untouched. */
MEGAPDF_API int megapdf_move_text_box(const megapdf_page* page, int object_index, double left, double bottom);

/** Removes and frees the box carrying `id`. Already gone counts as success, so an undo cannot fail. */
MEGAPDF_API int megapdf_remove_text_box(const megapdf_page* page, const unsigned short* id);

/**
 * Detached objects: page objects removed from their page but kept alive so an
 * undo can put them back byte-identical. The core owns them; restoring consumes the
 * handle, discarding frees it, and closing the document destroys the objects any handle
 * still holds — the handle then stays valid and dead, refusing everything but
 * megapdf_discard_detached(), which is still owed (#551; bindings hold these across an undo
 * stack, so a discard long after the close is the ordinary case).
 *
 * megapdf_detach_object() takes exactly the one object at `object_index`, whatever it
 * is; megapdf_restore_object() puts a one-object handle back at `object_index`, and
 * refuses a handle holding more (MEGAPDF_ERR_ARGUMENT, the handle stays valid).
 */
typedef struct megapdf_detached megapdf_detached;
MEGAPDF_API megapdf_detached* megapdf_detach_object(const megapdf_page* page, int object_index);
MEGAPDF_API int megapdf_restore_object(const megapdf_page* page, megapdf_detached* detached, int object_index);
MEGAPDF_API void megapdf_discard_detached(megapdf_detached* detached);

/**
 * Removes body text, a line or a single run, with the hidden copies drawn under it
 * (#136). Producers draw a line twice for fake bold, an outline or a shadow; PDFium's
 * text layer reads one copy and the other extracts as empty text, so it is never a run,
 * and removing only the runs leaves the line on the page. `object_indices` are the runs'
 * indices as the page is now, in any order, without repeats. Everything is taken at
 * once into one handle; megapdf_restore_detached() puts every object back where it was.
 *
 * NULL, with the page untouched, for a bad index, an object that is not text, a repeat,
 * or body text megapdf_text_editable() refuses (text boxes are not judged).
 */
MEGAPDF_API megapdf_detached* megapdf_detach_text_runs(const megapdf_page* page, const int* object_indices, size_t count);

/**
 * Undoes whatever produced `detached`, which it consumes: puts every object it holds
 * back at the index it had, and for a megapdf_set_text()/megapdf_set_line_text() handle
 * first takes the edited run off the page. The page must be as that call left it, apart
 * from changes already undone; later edits must be undone first. MEGAPDF_ERR_ARGUMENT,
 * with the page and the handle untouched, when the page is not the handle's or cannot be
 * as the call left it.
 */
MEGAPDF_API int megapdf_restore_detached(const megapdf_page* page, megapdf_detached* detached);

typedef struct megapdf_detached_part {
    int object_index;   /* where the object stood before it was taken, and where restoring puts it */
    int copy_of;        /* the object index of the run it is a hidden copy of; -1 for a run itself */
} megapdf_detached_part;

/** How many objects a handle holds, ascending by object index; for a recovery journal. */
MEGAPDF_API size_t megapdf_detached_count(const megapdf_detached* detached);
MEGAPDF_API int megapdf_detached_get(const megapdf_detached* detached, size_t index, megapdf_detached_part* out);

/* --------------------------------------------------------------------------
 * Contract 6: save, flatten and images (#110). File I/O, atomic replace and
 * verify-before-overwrite stay per platform; the core serialises to a caller
 * write callback exactly as FPDF_FILEWRITE does.
 * ----------------------------------------------------------------------- */

/** Receives one block of the serialised PDF; return 1 to continue, 0 to abort. */
typedef int (*megapdf_write_fn)(void* context, const void* data, size_t size);

/**
 * Commits any in-progress form edit and writes the whole document — always a
 * full rewrite (#97): PDFium's incremental save appends every loaded object,
 * which for a document whose pages were all loaded is the document again.
 * MEGAPDF_ERR_PDFIUM when PDFium refuses or the callback aborts.
 */
MEGAPDF_API int megapdf_save(const megapdf_document* document, megapdf_write_fn write, void* context);

/* --------------------------------------------------------------------------
 * Document security (#131). megapdf_open() takes the password; these report
 * what that open may do and write copies with new security or none.
 * ----------------------------------------------------------------------- */

/** What an open may do: the standard security handler's permission bits (ISO 32000-2, Table 22). */
#define MEGAPDF_PERMIT_PRINT          (1u << 2)
#define MEGAPDF_PERMIT_MODIFY         (1u << 3)
#define MEGAPDF_PERMIT_COPY           (1u << 4)
#define MEGAPDF_PERMIT_ANNOTATE       (1u << 5)
#define MEGAPDF_PERMIT_FILL_FORMS     (1u << 8)
#define MEGAPDF_PERMIT_ACCESSIBILITY  (1u << 9)
#define MEGAPDF_PERMIT_ASSEMBLE       (1u << 10)
#define MEGAPDF_PERMIT_PRINT_HIGH     (1u << 11)
#define MEGAPDF_PERMIT_ALL            (0xF3Cu)

/* `unsigned int`, not `unsigned long`: 32 bits on every platform the apps ship, so the
 * desktop binding marshals one layout on Windows and macOS alike. */
typedef struct megapdf_security {
    int encrypted;              /* 1 when the document has a security handler */
    int revision;               /* the standard handler's revision, 2-6; -1 when not encrypted */
    unsigned int permissions;   /* MEGAPDF_PERMIT_* this open may do; MEGAPDF_PERMIT_ALL unless restricted */
    int full_access;            /* 1 when this open may do everything, including change or remove the security:
                                   an unprotected document, an owner-password open, or one that restricts nothing */
} megapdf_security;

/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL document or `out`. */
MEGAPDF_API int megapdf_security_info(const megapdf_document* document, megapdf_security* out);

/**
 * The person chose to go on past an advisory permission the document's author
 * withheld (#558, ADR-004 decision 11). `allow` non-zero makes this open act as
 * though it had every advisory bit; zero puts it back.
 *
 * What it governs is exactly the advisory set: the modify bit that gates
 * megapdf_redact_apply(), and the assemble-or-modify and copy bits that gate
 * contract 10's page operations. It deliberately does NOT reach full access —
 * megapdf_save_with_security() and megapdf_save_without_security() still need the
 * owner password, because those two are not advisory: without the real credential
 * there is nothing to re-encrypt a copy with. Nor does it reach the *source*
 * document of megapdf_pages_import(): the choice is a statement about the document
 * the person opened, not about a second file handed to it.
 *
 * In memory, for this open only. It is not written to the document, it is not
 * remembered anywhere, and megapdf_security_info() keeps reporting the permissions
 * the file actually carries — the author's request survives the override, and
 * survives a save, which keeps the document's existing security.
 *
 * These bits were never access control: any tool holding the owner password can
 * clear them and plenty of tools ignore them outright. The core honours them so an
 * app can report the author's request; this call is how an app says the person was
 * told and chose to continue anyway.
 *
 * MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL document.
 */
MEGAPDF_API int megapdf_security_override(megapdf_document* document, int allow);

/**
 * Writes a copy encrypted with AES-256 (the standard handler at revision 6)
 * under new passwords, in place of any security the document had. The user
 * password (UTF-8; NULL or empty for none) opens the copy with `permissions`
 * (MEGAPDF_PERMIT_*); the owner password opens it with all of them, and NULL or
 * empty means the same as the user password. The document itself keeps the
 * security it was opened with, so megapdf_open_like() does not open the copy;
 * open it with the new password. Needs full access: MEGAPDF_ERR_RESTRICTED
 * otherwise. Needs MegaPDF's PDFium patch 0010.
 */
MEGAPDF_API int megapdf_save_with_security(const megapdf_document* document, const char* user_password_utf8,
                                           const char* owner_password_utf8, unsigned int permissions,
                                           megapdf_write_fn write, void* context);

/** Writes a copy with no security at all. Needs full access: MEGAPDF_ERR_RESTRICTED otherwise. */
MEGAPDF_API int megapdf_save_without_security(const megapdf_document* document, megapdf_write_fn write,
                                              void* context);

/** Commits form edits, then bakes every page's annotations and fields into its content. */
MEGAPDF_API int megapdf_flatten_all(const megapdf_document* document);

typedef struct megapdf_image_info {
    int page_index;
    int object_index;
    int pixel_width;         /* stored resolution */
    int pixel_height;
    double display_width;    /* points, as placed */
    double display_height;
    long long stored_bytes;  /* the raw stream length */
} megapdf_image_info;

typedef struct megapdf_images megapdf_images;

/** Every raster image object in the document, page by page; a snapshot. */
MEGAPDF_API megapdf_images* megapdf_images_load(const megapdf_document* document);
MEGAPDF_API void megapdf_images_free(megapdf_images* images);
MEGAPDF_API size_t megapdf_image_count(const megapdf_images* images);
MEGAPDF_API int megapdf_image_get(const megapdf_images* images, size_t index, megapdf_image_info* out);

/** The image object rendered at width × height pixels (BGRA; see megapdf_image_*). NULL on failure. */
MEGAPDF_API megapdf_image* megapdf_render_image(const megapdf_document* document, int page_index, int object_index,
                                                int width, int height);

/** Replaces the image object's data with a JPEG stream (DCTDecode), inline. */
MEGAPDF_API int megapdf_replace_image_jpeg(const megapdf_document* document, int page_index, int object_index,
                                           const unsigned char* jpeg, size_t length);

/**
 * Encodes BGRA pixels as JPEG at `quality` (0..1) into a buffer the callback owns;
 * return 1 with `out_jpeg`/`out_length` set, 0 on failure. The core calls `release`
 * with the buffer when it is done with it.
 */
typedef int (*megapdf_jpeg_encode_fn)(void* context, const unsigned char* bgra, int width, int height, double quality,
                                      unsigned char** out_jpeg, size_t* out_length);
typedef void (*megapdf_jpeg_release_fn)(void* context, unsigned char* jpeg);

/**
 * Shrink-for-email (SDD §3.7), the decision rules in one place: every image is
 * re-encoded at 150 dpi of its placed size, quality 0.75, unless it is already
 * about the right resolution and under 100 KB, under 8 KB, or under 8 px on a
 * side, or unless the re-encode would save less than 10%. `out_replaced` counts
 * the images replaced. Callers work on a copy: this degrades quality by design.
 */
MEGAPDF_API int megapdf_shrink_images(const megapdf_document* document, megapdf_jpeg_encode_fn encode,
                                      megapdf_jpeg_release_fn release, void* context, int* out_replaced);

/* --------------------------------------------------------------------------
 * Contract 7: render policy (#111, #93). Bitmap allocation and presentation stay
 * native; the size clamp, the flags, form-field drawing and "PDFium refused"
 * live here.
 * ----------------------------------------------------------------------- */

/** Longest side any page raster may have (the common GPU texture limit). */
#define MEGAPDF_RENDER_MAX_SIDE 16384
/** Most pixels any page raster may have: 32 MP is 128 MB of BGRA. */
#define MEGAPDF_RENDER_MAX_PIXELS 32000000LL

/**
 * The pixel size to render at for a page that would ideally be ideal_width ×
 * ideal_height pixels: aspect ratio preserved, never below 1 × 1, never past the
 * side or megapixel limits. A poster-sized scan at 300% on a 2× display asks for
 * hundreds of megapixels that nothing on screen needs; the view scales the
 * raster up over the remaining distance.
 */
MEGAPDF_API void megapdf_render_size(double ideal_width, double ideal_height, int* out_width, int* out_height);

/** 1 when megapdf_render_size() would shrink this request. */
MEGAPDF_API int megapdf_render_is_capped(double ideal_width, double ideal_height);

/**
 * megapdf_render() flags. Bit 0 is the buffer's byte order; the page tints (#509,
 * #168's reading mode, docs/reading-mode-plan.md section 2 tier 2) are bits above it,
 * so a caller ORs a tint onto the byte order it already passes. New flag values, not
 * new parameters: the "frozen struct, grown by new fields/enum values, never new
 * parameters" rule this header uses throughout (:218, :1328) is what keeps every
 * existing binding compiling and behaving exactly as before, desktop, iOS and Android
 * alike, with a platform opting in by setting a bit.
 */
enum {
    MEGAPDF_RENDER_BGRA = 0,   /* PDFium's native byte order (Windows, macOS, iOS) */
    MEGAPDF_RENDER_RGBA = 1,   /* byte-reversed, for Android's ARGB_8888 buffers */
    /**
     * Sepia: the rendered page multiplied toward a warm paper white. White becomes
     * #F4ECD8, black stays black, and every value between keeps its ordering, so the
     * page reads as paper while text keeps the contrast it needs to be read.
     */
    MEGAPDF_RENDER_SEPIA = 2,
    /**
     * Night: luminance inverted, hue kept. White becomes #1A1A1A and black becomes a
     * light grey, so black-on-white text reads as light-on-dark, while a coloured
     * element keeps its hue rather than turning into its RGB complement the way a flat
     * invert would -- brand blue stays blue.
     *
     * This inverts everything the page drew, photographs included: a photo reads as a
     * negative, and a scan inverts the way someone reading at night wants. That is the
     * decision recorded on #168, not a defect to fix -- Edge's and Chrome's PDF dark
     * modes do the same, and the apps' settings copy says so ("Night inverts the page,
     * pictures included").
     *
     * The alternative, and why it is not this: FPDF_COLORSCHEME with
     * FPDF_RenderPageBitmapWithColorScheme_Start (fpdfview.h, fpdf_progressive.h)
     * recolours text and paths and leaves images alone, but it flattens every path to
     * a single fill colour, so a diagram loses its colours, and there is no
     * FPDF_FFLDraw variant taking a colour scheme, so form fields would draw in
     * daylight colours over a night page. It stays the documented alternative if
     * "leave images alone" is ever wanted; it is not offered as a toggle here.
     */
    MEGAPDF_RENDER_NIGHT = 4
};

/**
 * Renders the page into the caller's width × height buffer of `stride` bytes per
 * row (at least width × 4): white ground, page content with annotations and LCD
 * text, then live form-field values, then the MEGAPDF_RENDER_SEPIA or
 * MEGAPDF_RENDER_NIGHT tint when one is asked for. MEGAPDF_ERR_ARGUMENT for a null
 * page or buffer, a non-positive size, a size past the clamp, or both tints at once
 * (page colours are one choice of three, so both bits set is a caller bug worth
 * catching rather than silently resolving); MEGAPDF_ERR_PDFIUM when PDFium refuses
 * the bitmap. Never crashes on a refusal.
 *
 * A tint is a post-pass over the buffer, so it costs one linear pass over at most
 * MEGAPDF_RENDER_MAX_PIXELS and changes nothing in the document: page colours are a
 * way of looking at a page, never written to the file. Render caches must key on the
 * tint, because the same page at the same size is now three different rasters.
 */
MEGAPDF_API int megapdf_render(const megapdf_page* page, void* buffer, int width, int height, int stride,
                               unsigned int flags);

/**
 * The longest side the full page would have to be rastered at for a clip render to
 * fill its buffer. A clip is a window onto a page drawn at a scale the window sets, so
 * a 2 pt window filling a 1,024 px buffer asks PDFium for a page drawn 512x larger than
 * itself; past this the implied matrix stops being worth trusting and the request is a
 * caller bug (a degenerate rectangle, or crop space confused with device pixels), not a
 * picture anyone wants. Nothing of this size is ever allocated: PDFium rasterises the
 * caller's buffer and no more -- see megapdf_render_clip.
 */
#define MEGAPDF_RENDER_CLIP_MAX_IMPLIED_SIDE 1000000LL

/**
 * Renders one region of the page -- `crop_space_rect`, in the crop space every contract
 * here reports (points, bottom-left origin, /UserUnit applied, turned with the page's
 * /Rotate) -- into the caller's width x height buffer, scaled to fill it. Everything
 * else is megapdf_render's recipe exactly: white ground, page content with annotations
 * and LCD text, live form-field values, then the tint. Same flags, same byte order,
 * same clamp on the buffer, same errors, and the same "never crashes on a refusal".
 *
 * Why it exists (#514, docs/reading-mode-plan.md section 3 item 2): a reflow view shows a
 * block, a figure or a form region "as the page" inside otherwise reflowed text. Without
 * this, that costs a whole-page raster per region -- on a phone, for a region that is often
 * a twentieth of the page. PDFium clips natively, no patch and no second bitmap needed: a
 * negative start_x/start_y with an oversize size_x/size_y places the page's full raster so
 * that only the wanted region lands inside the bitmap, and PDFium rasterises the bitmap,
 * not the page, so cost follows the buffer rather than the implied page.
 *
 * The aspect ratio is the caller's business: the rectangle is mapped onto the whole buffer,
 * so a buffer shaped unlike the rectangle stretches it. Ask for a buffer shaped like the
 * rectangle (megapdf_render_size over the rectangle's own points) when that matters.
 *
 * MEGAPDF_ERR_ARGUMENT for a null page, buffer or rectangle, a non-positive size, a stride
 * under width x 4, a size past the clamp, both tints at once, an empty or inverted
 * rectangle (right <= left or top <= bottom), or an implied full-page side past
 * MEGAPDF_RENDER_CLIP_MAX_IMPLIED_SIDE. MEGAPDF_ERR_PDFIUM when PDFium refuses the bitmap.
 *
 * A rectangle reaching outside the page is legal and is not clamped: the part of the buffer
 * no page covers stays the white ground, which is what a block whose bounds touch the page
 * edge should look like. The rectangle is NOT snapped to whole pixels either -- it is used
 * as given, so a caller can scroll a clip smoothly.
 */
MEGAPDF_API int megapdf_render_clip(const megapdf_page* page, const megapdf_rect* crop_space_rect, void* buffer,
                                    int width, int height, int stride, unsigned int flags);

/* --------------------------------------------------------------------------
 * Phase 3: body-text editing, written once (#112, SDD §3.1). The tiers:
 *   1. the run's own font covers the new text → edit in place;
 *   2. it does not (a subset-embedded font only carries the glyphs the document
 *      already uses, and PDFium would silently draw notdef boxes) → replace the
 *      run with the closest standard face, same index, matrix, colour and marks;
 *   3. rasterised text has no run to edit — the caller reports that; nothing here
 *      pretends otherwise.
 * Undo is detach-and-restore (contract 5); megapdf_insert_text_run replays a
 * journalled restore after a crash.
 * ----------------------------------------------------------------------- */

enum {
    MEGAPDF_EDIT_IN_PLACE = 0,     /* tier 1: the document's own font rendered the new text */
    MEGAPDF_EDIT_SUBSTITUTED = 1   /* tier 2: a similar standard face was used; the UI shows a notice */
};

/** Flags for megapdf_set_text(). */
enum {
    /** Skip tier 1 and substitute — the test hook the desktop suite uses. */
    MEGAPDF_SET_TEXT_FORCE_SUBSTITUTE = 1
};

/**
 * Sets a text run's text. `out_outcome` receives MEGAPDF_EDIT_IN_PLACE (the run's
 * own font drew it) or MEGAPDF_EDIT_SUBSTITUTED (a standard face did).
 *
 * The original object is never modified. Either way the edited run is a new
 * text object at `object_index` — same font size, matrix, colours, render mode
 * and text-box identity — and the original leaves the page detached, together with
 * any hidden copy of the run drawn under it (#136; see megapdf_detach_text_runs). They
 * are handed back through `out_replaced` when non-NULL, so an undo restores them
 * byte-identical with megapdf_restore_detached(), which takes the edited run off
 * first. With `out_replaced` NULL they are freed. (PDFium cannot read a text object's
 * character codes back, so an edit made on the original itself could never be undone
 * exactly, #117.) When the run had no hidden copy the handle holds the one original,
 * and detaching the edited run and megapdf_restore_object() at `object_index` undoes
 * the edit as well.
 *
 * MEGAPDF_ERR_ARGUMENT for a non-text object, empty text or an unknown flag;
 * MEGAPDF_ERR_NO_FONT when not even the substitute can draw the text;
 * MEGAPDF_ERR_LAYOUT when megapdf_text_editable() says no — the document is untouched.
 */
MEGAPDF_API int megapdf_set_text(const megapdf_page* page, int object_index, const unsigned short* text,
                                 unsigned int flags, int* out_outcome, megapdf_detached** out_replaced);

/**
 * Retypes a visual line: megapdf_set_text() on `object_indices[0]`, and the line's other
 * runs removed, all in one call, with the hidden copies of every run (#136). Indices as
 * the page is now, without repeats. One handle holds every original; undo it with
 * megapdf_restore_detached(). The same errors as megapdf_set_text(), with the page
 * untouched; MEGAPDF_ERR_ARGUMENT too for a bad or repeated index among the rest, and
 * MEGAPDF_ERR_LAYOUT when megapdf_text_editable() refuses any of the runs.
 */
MEGAPDF_API int megapdf_set_line_text(const megapdf_page* page, const int* object_indices, size_t count,
                                      const unsigned short* text, unsigned int flags, int* out_outcome,
                                      megapdf_detached** out_replaced);

/**
 * Inserts a text object at `object_index` with its baseline starting at
 * (left, baseline) in crop space, in the standard face closest to `font_name`
 * (any name; see megapdf_map_to_standard_font). Untagged — this recreates body
 * text from the recovery journal, not a text box.
 */
MEGAPDF_API int megapdf_insert_text_run(const megapdf_page* page, int object_index, const unsigned short* text,
                                        const char* font_name, double font_size, double left, double baseline);

/**
 * Whether the text object at `object_index` can be edited or removed without PDFium
 * changing anything else about the page (#118): 1 yes, 0 no, MEGAPDF_ERR_ARGUMENT for
 * a bad page or index.
 *
 * Changing a text object makes PDFium rewrite the content stream that holds it, and
 * its writer drops text state it has no syntax for — character and word spacing,
 * horizontal scaling, rise — and turns colour spaces into device colour. On many
 * real documents that moves or restyles text the user never touched. The answer
 * comes from a dry run on a copy of the page: rewrite the stream there, with the
 * object's hidden copies (#136), save, reopen, and compare the render and every text
 * object's position and text. Cached per page and object until the page next changes
 * (#137). megapdf_set_text(), megapdf_set_line_text(), megapdf_detach_text_runs() and
 * megapdf_detach_object() (for body text) refuse what this refuses, so the apps can ask
 * first and say so when a line is tapped. megapdf_text_editable_reason() says why.
 */
MEGAPDF_API int megapdf_text_editable(const megapdf_page* page, int object_index);

/** Why megapdf_text_editable() answered as it did (#128): megapdf_layout_verdict.cause. */
enum {
    MEGAPDF_LAYOUT_OK = 0,              /* editable: the rewrite changed nothing past the budgets below */
    MEGAPDF_LAYOUT_RENDER = 1,          /* more than 0.05% of the page's pixels would look different */
    MEGAPDF_LAYOUT_TEXT_MOVED = 2,      /* a text object's bounds would move by more than 0.5 pt */
    MEGAPDF_LAYOUT_TEXT_CHANGED = 3,    /* the page would have a different number of text objects, or different text */
    MEGAPDF_LAYOUT_REWRITE_FAILED = 4   /* PDFium could not rewrite, save or reopen the copy of the page */
};

/** Where the changed pixels are: megapdf_layout_verdict.where, a bitmask. */
enum {
    MEGAPDF_LAYOUT_WHERE_OBJECT = 1,    /* on the judged object (with its hidden copies), padded by 2 pt */
    MEGAPDF_LAYOUT_WHERE_TEXT = 2,      /* elsewhere, on another text object's bounds */
    MEGAPDF_LAYOUT_WHERE_OTHER = 4      /* elsewhere, off every text object: images, paths, forms, shadings */
};

/**
 * The dry run's verdict behind megapdf_text_editable(), with the numbers it saw.
 *
 * `cause` names the check that refused. When the text check and the render check both
 * fail, the text cause is given: a moved or changed run explains the pixels. The pixel
 * numbers are always filled in, also for an editable object (a change within the budget).
 * `changed_pixels` counts pixels whose |dR|+|dG|+|dB| exceeds 60 in a render of the page at
 * 72 dpi, halved until it has at most a million pixels; `total_pixels` is that render's size.
 * `max_shift_pt` is the largest move of any text object's bounds, text objects paired in
 * content order; 0 when their number changed. With MEGAPDF_LAYOUT_REWRITE_FAILED every
 * number is 0.
 */
typedef struct megapdf_layout_verdict {
    int editable;          /* 1 or 0, as megapdf_text_editable() */
    int cause;             /* MEGAPDF_LAYOUT_* */
    int where;             /* MEGAPDF_LAYOUT_WHERE_* bits; 0 when no pixel changed */
    int changed_pixels;
    int total_pixels;
    double max_shift_pt;
} megapdf_layout_verdict;

/**
 * megapdf_text_editable() with its reason: the same dry run, the same cache (#137), and
 * the same return value (1, 0 or MEGAPDF_ERR_ARGUMENT, also for a NULL `out`). `out` is
 * filled in when the return is 1 or 0. A line judged together in one dry run (by
 * megapdf_set_line_text() or megapdf_detach_text_runs()) caches the line's verdict for each
 * of its runs when it passes; when it is refused, each run is judged alone and keeps its own.
 */
MEGAPDF_API int megapdf_text_editable_reason(const megapdf_page* page, int object_index, megapdf_layout_verdict* out);

/**
 * The verdict behind this thread's most recent layout refusal (#128). megapdf_set_text(),
 * megapdf_set_line_text(), megapdf_detach_text_runs() and megapdf_detach_object() reset it
 * to editable (1, MEGAPDF_LAYOUT_OK, zeros) when called, and set it when the guard refuses
 * them. So read it straight after one of them, on the same thread: after
 * MEGAPDF_ERR_LAYOUT it holds the refused object's verdict (the first refused run of a
 * line), and after a NULL detach `editable == 0` tells a layout refusal from any other
 * failure. MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL `out`.
 */
MEGAPDF_API int megapdf_last_layout_verdict(megapdf_layout_verdict* out);

/**
 * Whether regenerating the page's content would change how the page looks (#139): 1 no
 * (the page keeps its look), 0 yes, MEGAPDF_ERR_ARGUMENT for a bad page or a NULL `out`.
 * `out` is filled in when the return is 1 or 0.
 *
 * Every change that is not a body-text edit also makes PDFium rewrite the page's content:
 * adding, moving or removing a whiteout or a text box, megapdf_detach_object() on anything
 * but body text, megapdf_restore_object(), megapdf_replace_image_jpeg(), flattening. Those
 * changes are never refused; this lets the apps warn first. The answer is the
 * megapdf_text_editable() dry run with no text object changed: mark one object dirty on a
 * copy of the page, rewrite it, save, reopen, and compare the render and every text object
 * with a reopened copy of the page as it was, by the same budgets. `cause` and `where` are
 * as megapdf_layout_verdict describes; `where` never has MEGAPDF_LAYOUT_WHERE_OBJECT. A page
 * with no objects has nothing to rewrite and keeps its look. Cached per page until the page
 * next changes (#137).
 *
 * The same as megapdf_page_regeneration_verdict_cancellable() with no cancel flag, so it also
 * lets other calls run between its stages and returns MEGAPDF_ERR_CANCELLED when the
 * document is closed while it runs.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict(const megapdf_page* page, megapdf_layout_verdict* out);

/**
 * megapdf_page_regeneration_verdict() for a check started early, in the background (#145).
 *
 * The dry run lets go of the core lock between its stages (copy and render, save, rewrite,
 * save again, compare), so other calls on any document — rendering, an edit, a save — wait at
 * most one stage, not the whole run. Between stages it stops when `cancel` (may be NULL) has
 * been raised or the page's document is being closed, and returns MEGAPDF_ERR_CANCELLED with
 * `out` untouched; megapdf_close() waits for a running check to stop. The page handle may be
 * closed while the check runs; its document must stay open until the call has begun.
 *
 * The answer is cached as megapdf_page_regeneration_verdict()'s is, except when the page
 * changed while the check ran: then it describes the page as it was and is not cached. Two
 * checks of one page at once each do the work; the apps keep one per page.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict_cancellable(const megapdf_page* page, const megapdf_cancel* cancel,
                                                             megapdf_layout_verdict* out);

/**
 * The cached page verdict, without running anything (#145): 1 or 0 as
 * megapdf_page_regeneration_verdict() would return, MEGAPDF_ERR_NOT_JUDGED when the page has
 * not been judged since it last changed, MEGAPDF_ERR_ARGUMENT for a bad page or a NULL `out`.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict_cached(const megapdf_page* page, megapdf_layout_verdict* out);

/** 1 when `base_name` is a subset-embedded font's name: six capitals, a plus sign, the name. */
MEGAPDF_API int megapdf_is_subset_font_name(const char* base_name);

/**
 * The standard-14 face closest to `original_name` (bold/italic from the name;
 * Courier for monospace, Times for serif, Helvetica otherwise). Writes a
 * NUL-terminated name into `out` when it fits; returns its length without the NUL.
 */
MEGAPDF_API size_t megapdf_map_to_standard_font(const char* original_name, char* out, size_t capacity);

/* --------------------------------------------------------------------------
 * Contract 8: redaction (#173, ADR-005). A redaction removes content; a whiteout
 * covers it. Every platform marks areas, applies them once, and shows the report.
 *
 * Marks are the core's own: they are never page objects and never reach the file, so
 * a document saved with marks on it cannot carry them, and marking costs nothing —
 * no content regeneration, no invalidated layout verdicts (#137). The apps draw the
 * translucent box in the overlay layer where find highlights already live.
 *
 * Coordinates are crop space, as everywhere else.
 * ----------------------------------------------------------------------- */

typedef struct megapdf_redaction_area {
    int mark_id;             /* stable for the mark's life; never reused by the document */
    megapdf_rect bounds;     /* crop space */
} megapdf_redaction_area;

/**
 * Marks `area` on the page for redaction. The mark's id comes back in `out_mark_id`
 * (may be NULL). MEGAPDF_ERR_ARGUMENT for a NULL page or area, or an empty one (a
 * rectangle with no width or height); MEGAPDF_ERR_MEMORY when the core cannot allocate.
 * Nothing on the page changes.
 */
MEGAPDF_API int megapdf_redaction_mark(const megapdf_page* page, const megapdf_rect* area, int* out_mark_id);

/**
 * Marks the text the user selected: every rectangle a selection covers becomes its own
 * mark, one per line the selection spans, each grown to the glyphs it touches so a mark
 * always covers whole glyphs. `selection` is the dragged rectangle in crop space.
 *
 * NOT count-then-fill, unlike everything else here: this call MAKES the marks, so calling
 * it a second time to size a buffer would make them twice. It returns how many it made,
 * fills up to `capacity` of their ids, and 0 means the selection covers no text — the
 * caller then marks the rectangle itself with megapdf_redaction_mark(). A caller whose
 * buffer was too small reads the rest back with megapdf_redaction_marks().
 */
MEGAPDF_API size_t megapdf_redaction_mark_text(const megapdf_page* page, const megapdf_rect* selection,
                                               int* out_mark_ids, size_t capacity);

/** The page's marks, in the order they were made; count-then-fill. */
MEGAPDF_API size_t megapdf_redaction_marks(const megapdf_page* page, megapdf_redaction_area* out, size_t capacity);

/** Moves or resizes the mark. MEGAPDF_ERR_ARGUMENT when the page has no such mark. */
MEGAPDF_API int megapdf_redaction_move_mark(const megapdf_page* page, int mark_id, const megapdf_rect* area);

/** Removes the mark. Already gone counts as success, so an undo cannot fail. */
MEGAPDF_API int megapdf_redaction_remove_mark(const megapdf_page* page, int mark_id);

/** How many marks the whole document carries: what the save confirmation asks. */
MEGAPDF_API size_t megapdf_redaction_mark_count(const megapdf_document* document);

/** Drops every mark on the document. */
MEGAPDF_API void megapdf_redaction_clear(const megapdf_document* document);

/** Options for megapdf_redact_apply(); zero-initialise and set what differs from the defaults. */
typedef struct megapdf_redact_options {
    unsigned int colour;          /* 0xRRGGBB of the box drawn where the content was; 0 is black, the default */
    int leave_area_bare;          /* 1: draw no box at all. 0 (default): a plain filled path, no annotation */
    int keep_metadata;            /* 1: leave /Info and XMP alone. 0 (default): remove them, as a redaction implies */
    int keep_matching_outline;    /* 1: leave outline entries alone. 0 (default): remove those carrying removed text */
} megapdf_redact_options;

/** Why megapdf_redact_apply() refused: megapdf_redaction_refusal.reason. */
enum {
    MEGAPDF_REDACT_TYPE3_FONT = 1,      /* a partly covered run in a Type 3 font, whose glyphs are content streams */
    MEGAPDF_REDACT_FONT_CANNOT_REDRAW,  /* the glyphs outside the area cannot be drawn back in the run's own font (#116, #130) */
    MEGAPDF_REDACT_LAYOUT,              /* the guard saw something outside the area move or change (#118, #128) */
    MEGAPDF_REDACT_SHARED_FORM,         /* a form XObject more than one object draws could not be copied first */
    MEGAPDF_REDACT_IMAGE,               /* an image's stored pixels could not be read or written back */
    MEGAPDF_REDACT_ANNOTATION,          /* an annotation or form field could not be removed with its value */
    MEGAPDF_REDACT_PDFIUM               /* PDFium refused a step; see the refusal's message */
};

typedef struct megapdf_redaction_refusal {
    int page_index;
    int reason;                  /* MEGAPDF_REDACT_* */
    megapdf_rect area;           /* the marked area it was refused for, crop space */
} megapdf_redaction_refusal;

/** What a completed apply removed. Zero for a refused apply, which removes nothing. */
typedef struct megapdf_redaction_counts {
    int areas;                /* marks applied */
    int pages;                /* pages they were on */
    int characters;           /* glyphs removed from content streams */
    int text_runs;            /* text objects removed whole */
    int partial_runs;         /* text objects rewritten without their covered glyphs */
    int hidden_copies;        /* the #136 copies removed or rewritten with them */
    int images;               /* image objects whose stored pixels were overwritten */
    int inline_images;
    int soft_masks;
    int paths;                /* path objects removed or clipped */
    int shadings;
    int form_xobjects;        /* forms recursed into */
    int annotations;
    int form_fields;
    int links;
    int outline_entries;
    int structure_entries;    /* /ActualText and /Alt cleared */
    int page_labels;
    int metadata_fields;      /* /Info entries and XMP packets removed */
    int attachments;          /* embedded files whose bytes carried the removed text */
} megapdf_redaction_counts;

typedef struct megapdf_redaction_applied {
    int page_index;
    megapdf_rect marked;     /* the union of the page's marked areas */
    megapdf_rect affected;   /* it, grown to the bounds of everything removed from the page */
} megapdf_redaction_applied;

typedef struct megapdf_redaction_report megapdf_redaction_report;

/**
 * Applies every mark on the document, then drops them. Needs MEGAPDF_PERMIT_MODIFY
 * (ADR-004 decision 2): MEGAPDF_ERR_RESTRICTED otherwise.
 *
 * It fails closed. The work runs in three phases:
 *   1. plan — read only, over every marked page: everything intersecting is classified
 *      remove, rewrite, clip or refuse;
 *   2. rehearse — the #118 dry run on a copy of each marked page, with the marked areas
 *      masked out of the render compare, so the guard proves that nothing OUTSIDE the
 *      areas moved or changed;
 *   3. execute — the same plan on the document, then a check in process that the areas
 *      now extract no text and read back the redaction colour.
 * A refusal in phase 1 or 2 returns MEGAPDF_ERR_REDACT with the document untouched and
 * the marks still on it, and the report names every page, area and reason. A failure in
 * phase 3 — which phase 2 says cannot happen — poisons the document: megapdf_save() and
 * every megapdf_save_*() on it return MEGAPDF_ERR_REDACT from then on, so a half-redacted
 * document can never be written. A poisoned document can only be closed.
 *
 * Apply also frees every megapdf_detached handle the document holds: those keep removed
 * objects alive for an undo (contract 5), and after a redaction they would be the redacted
 * content, one megapdf_restore_detached() from the page. The apps drop their undo stacks
 * and rewrite the recovery journal in the same step (#145).
 *
 * `options` may be NULL for the defaults. `out_report` may be NULL; when it is not, a
 * report comes back on success AND on a refusal, and the caller frees it with
 * megapdf_redaction_report_free(). MEGAPDF_OK, MEGAPDF_ERR_REDACT, MEGAPDF_ERR_RESTRICTED,
 * MEGAPDF_ERR_ARGUMENT (a NULL document) or MEGAPDF_ERR_MEMORY.
 */
MEGAPDF_API int megapdf_redact_apply(megapdf_document* document, const megapdf_redact_options* options,
                                     megapdf_redaction_report** out_report);

MEGAPDF_API void megapdf_redaction_report_free(megapdf_redaction_report* report);

/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL report or `out`. */
MEGAPDF_API int megapdf_redaction_report_counts(const megapdf_redaction_report* report, megapdf_redaction_counts* out);

/** The refusals, in page order; count-then-fill. A completed apply has none. */
MEGAPDF_API size_t megapdf_redaction_refusals(const megapdf_redaction_report* report, megapdf_redaction_refusal* out,
                                              size_t capacity);

/**
 * What each redacted page actually lost, page by page; count-then-fill.
 *
 * `marked` is the union of the page's marked areas. `affected` is that grown to the bounds
 * of everything the redaction removed from the page, which can reach past the mark: a glyph
 * is removed when its box intersects the area, and a glyph cannot be half removed, so one
 * straddling the edge — commonly, any glyph of rotated text, whose box is much larger than
 * its ink — takes its part outside the mark with it. The box drawn covers the mark, not the
 * affected area, because covering more would cover content that is still in the file, which
 * is the whiteout mistake #173 exists to stop. So `affected` is where the page may look
 * different, and the tests and the corpus battery judge "unchanged outside" against it.
 */
MEGAPDF_API size_t megapdf_redaction_applied_areas(const megapdf_redaction_report* report,
                                                   megapdf_redaction_applied* out, size_t capacity);

/**
 * A short English sentence for the refusal at `index` — what could not be removed and why.
 * UTF-8, NUL-terminated, count-then-fill in bytes including the terminator. The bindings
 * show their own wording from the reason code; this is for logs and for the tests.
 */
MEGAPDF_API size_t megapdf_redaction_refusal_message(const megapdf_redaction_report* report, size_t index,
                                                     char* out, size_t capacity);

/**
 * 1 when a redaction failed halfway on this document and it may no longer be saved.
 * 0 otherwise, including for a document that was redacted successfully.
 */
MEGAPDF_API int megapdf_redaction_poisoned(const megapdf_document* document);

/* --------------------------------------------------------------------------
 * Contract 9: document structure (#142, #353, #358, #168; SDD §3.9, §6.2 contract 6).
 * Blocks in reading order over a page range, from the structure tree when the
 * page is tagged and the tree is trustworthy (#358), otherwise inferred from
 * PDFium's text page (the heuristic path, #353). Per page, never mixed:
 * megapdf_structure_page_source() says which ran.
 *
 * Trust rule (#358, design §1.1): a page's tree is used only when at least 90%
 * of its characters sit in a tree element of a known standard type (or in an
 * /Artifact marked-content sequence, which is furniture) and the tree's
 * depth-first order references no marked content twice. Otherwise the page
 * takes the heuristic path with its confidence capped at 80. A tagged page's
 * confidence is the tree's character coverage (0..100). Tagged pages give
 * headings at their tagged level (H1..H6, or nesting depth for a bare H),
 * LIST_ITEM depth from /L nesting with the /Lbl as the marker, TABLE_ROW blocks
 * (level = 1-based row number, continues = 1 after the table's first row; cells
 * marked by MEGAPDF_SPAN_CELL_START, header cells by MEGAPDF_SPAN_CELL_HEADER;
 * the block's text is the cells joined by single spaces), FIGURE blocks with the
 * /Figure's /Alt text, and MEGAPDF_SPAN_LINK on characters under a /Link.
 *
 * A page range, not a page: body-size estimation, running header/footer
 * detection and paragraph continuation all need more than one page. A
 * one-page load is legal and gives page-local answers (only the page-number
 * furniture rule, no running-header detection, no cross-page continuation).
 *
 * The text comes from FPDF_TEXTPAGE — the same source megapdf_search_page
 * reads — not from megapdf_text_load's page-level text objects, which miss
 * text a form XObject draws (megapdf_core.cpp's ReadObjectTexts reads only
 * page objects; see tools/leakcheck's OccurrencesInText for the same trap
 * documented from the redaction side). megapdf_text_load and its line-building
 * are unchanged: existing text_runs.txt goldens stay valid.
 *
 * A block's text is canonical (SDD §6.2 contract 6): exactly the
 * concatenation of its spans, with word spacing, hyphen-joining and #136's
 * hidden-copy removal already applied. No consumer re-joins it.
 * ----------------------------------------------------------------------- */

typedef struct megapdf_structure megapdf_structure;

/** Flags for megapdf_structure_load(). */
enum {
    MEGAPDF_STRUCTURE_DEFAULT = 0,
    MEGAPDF_STRUCTURE_HEURISTIC_ONLY = 1,   /* ignore the structure tree even when present (measurement, --heuristic) */
    MEGAPDF_STRUCTURE_KEEP_FURNITURE = 2,   /* headers, footers and page numbers stay as blocks instead of being dropped */
    MEGAPDF_STRUCTURE_ALL_FIELDS = 4        /* FIELD blocks for empty text fields and unchecked boxes too */
};

typedef enum megapdf_block_kind {
    MEGAPDF_BLOCK_HEADING = 1,     /* level 1..6 */
    MEGAPDF_BLOCK_PARAGRAPH = 2,
    MEGAPDF_BLOCK_LIST_ITEM = 3,   /* level = nesting depth from 1; marker string separate from text */
    MEGAPDF_BLOCK_TABLE_ROW = 4,   /* tagged pages only: one block per /TR; level = 1-based row number, cells by span flags */
    MEGAPDF_BLOCK_FIGURE = 5,      /* an image object: object_index set (megapdf_render_image works on it); alt text when tagged */
    MEGAPDF_BLOCK_PAGE_IMAGE = 6,  /* a page with no usable text: the consumer shows the page itself */
    MEGAPDF_BLOCK_FURNITURE = 7,   /* a running header/footer/page number (only present with MEGAPDF_STRUCTURE_KEEP_FURNITURE) */
    MEGAPDF_BLOCK_FIELD = 8        /* a form field, from the existing megapdf_form_fields_load (contract 3) */
} megapdf_block_kind;

/** megapdf_structure_page_source(): which path produced a page's blocks. */
enum {
    MEGAPDF_STRUCTURE_SOURCE_HEURISTIC = 0,
    MEGAPDF_STRUCTURE_SOURCE_TAGGED = 1     /* the page's structure tree passed the trust rule (#358) */
};

typedef struct megapdf_block {
    int kind;             /* megapdf_block_kind */
    int level;            /* heading level (1..6), list nesting depth (from 1), or 0 */
    int page;             /* source page index, relative to the document, not the loaded range */
    megapdf_rect bounds;  /* crop space on that page; the whole page for PAGE_IMAGE */
    int object_index;     /* FIGURE: the image object's index; -1 otherwise */
    int continues;        /* 1 when this block continues the previous one (a paragraph split across a column or page) */
    int source;           /* MEGAPDF_STRUCTURE_SOURCE_* for the page this block is on */
    int confidence;       /* 0..100 for the page this block is on; see megapdf_structure_page_confidence() */
} megapdf_block;

/** megapdf_span.flags. */
enum {
    MEGAPDF_SPAN_BOLD = 1,
    MEGAPDF_SPAN_ITALIC = 2,
    MEGAPDF_SPAN_MONOSPACE = 4,
    MEGAPDF_SPAN_CELL_START = 8,   /* TABLE_ROW (tagged): this span starts a new cell */
    MEGAPDF_SPAN_LINK = 16,        /* tagged: the characters sit under a /Link element (the target URL is not exposed) */
    MEGAPDF_SPAN_CELL_HEADER = 32  /* TABLE_ROW (tagged): the cell this span starts was a /TH, not a /TD (#358) */
};

typedef struct megapdf_span {
    int flags;            /* MEGAPDF_SPAN_* */
    double font_size;     /* points, crop space (/UserUnit applied) */
    double size_ratio;    /* font_size over megapdf_structure_body_size() */
    megapdf_rect bounds;  /* crop space, on the block's page */
    int object_index;     /* the page-level text object this span's characters belong to; -1 for a form-XObject run */
} megapdf_span;

/** Which string megapdf_block_string() returns. */
typedef enum megapdf_block_field {
    MEGAPDF_BLOCK_TEXT = 0,    /* the block's whole text: exactly the concatenation of its spans */
    MEGAPDF_BLOCK_MARKER = 1,  /* the list marker as drawn ("•", "3.", "(b)"); a FIELD's fully qualified name; "" otherwise */
    MEGAPDF_BLOCK_ALT = 2      /* FIGURE alt text (tagged only); "" otherwise */
} megapdf_block_field;

/**
 * Infers document structure over pages [first_page, first_page + page_count), in reading
 * order. Returns NULL for a NULL document, an out-of-range or non-positive range, or when
 * the core cannot allocate. `cancel` may be NULL; a cancelled load returns NULL with
 * megapdf_last_error() unset and nothing leaked (#145's pattern — see megapdf_last_error()
 * for other failures, which set it).
 */
MEGAPDF_API megapdf_structure* megapdf_structure_load(megapdf_document* document, int first_page, int page_count,
                                                       unsigned int flags, const megapdf_cancel* cancel);
MEGAPDF_API void megapdf_structure_free(megapdf_structure* s);

/** The modal, character-count-weighted body font size over the loaded range, rounded to 0.5 pt. */
MEGAPDF_API double megapdf_structure_body_size(const megapdf_structure* s);

/** 0..100 for `page` (a document-relative index); MEGAPDF_ERR_ARGUMENT for a page outside the loaded range. */
MEGAPDF_API int megapdf_structure_page_confidence(const megapdf_structure* s, int page);

/** MEGAPDF_STRUCTURE_SOURCE_* for `page`; MEGAPDF_ERR_ARGUMENT for a page outside the loaded range. */
MEGAPDF_API int megapdf_structure_page_source(const megapdf_structure* s, int page);

MEGAPDF_API size_t megapdf_block_count(const megapdf_structure* s);
/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a bad handle, index or NULL `out`. */
MEGAPDF_API int megapdf_block_get(const megapdf_structure* s, size_t index, megapdf_block* out);
/** UTF-16 code units, no terminator, count-then-fill. 0 for a bad handle, index or field. */
MEGAPDF_API size_t megapdf_block_string(const megapdf_structure* s, size_t index, megapdf_block_field which,
                                        unsigned short* out, size_t capacity);

MEGAPDF_API size_t megapdf_block_span_count(const megapdf_structure* s, size_t index);
/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a bad handle, block index, span index or NULL `out`. */
MEGAPDF_API int megapdf_block_span_get(const megapdf_structure* s, size_t index, size_t span, megapdf_span* out);
/** UTF-16 code units, no terminator, count-then-fill. 0 for a bad handle, block index or span index. */
MEGAPDF_API size_t megapdf_block_span_string(const megapdf_structure* s, size_t index, size_t span,
                                             unsigned short* out, size_t capacity);

/**
 * The span's font family name as the document names it (#514, docs/reading-mode-plan.md
 * section 3 item 3), UTF-16 code units, no terminator, count-then-fill like every other
 * string accessor here. 0 for a bad handle, block index or span index, and 0 for a span
 * whose font PDFium cannot name -- a font with no /BaseFont, or a span of nothing but
 * synthetic separators. A subset-embedded font's name usually arrives with its six-letter
 * tag still on it ("ABCDEF+Minion-Regular"): the tag is PDF 32000-1 9.6.4's, it is part of
 * the name in the file, and it is handed over as the document wrote it rather than stripped
 * here, because a consumer matching installed faces wants to try both and only the consumer
 * knows what it has installed.
 *
 * This is a name, not a face. A subset or symbolic font will match nothing installed, and
 * the plan's own risk note says what to do then: fall back to the platform serif or sans by
 * the span's MEGAPDF_SPAN_BOLD/ITALIC/MONOSPACE flags. Embedded-font extraction
 * (FPDFFont_GetFontData) is deliberately not here -- it is its own problem, with its own
 * licensing-flag question.
 *
 * A span is a same-style run, and since #514 the family is part of "same style": a run that
 * changes family mid-way is two spans even when weight, slant, pitch and size all stay put,
 * so this name describes every character of the span and not just its first. (Before #514 a
 * family change could hide inside one span, because only the page object, the three style
 * flags, the link flag and the size were compared. The split can only refine spans, never
 * merge them, so a block's text is unchanged -- it is still exactly the concatenation of its
 * spans.)
 */
MEGAPDF_API size_t megapdf_block_span_font(const megapdf_structure* s, size_t index, size_t span,
                                           unsigned short* out, size_t capacity);

/* --------------------------------------------------------------------------
 * Text and Markdown writers (#142, #355, #357), over contract 9's blocks. `format` picks the
 * output kind (MEGAPDF_WRITE_TEXT or MEGAPDF_WRITE_MARKDOWN) as another `megapdf_write_options`
 * field, without breaking this signature or any existing caller — the same "frozen struct,
 * grown by new fields/enum values, never new parameters" shape contract 9 itself uses.
 * `megapdf_cli extract` (core/cli/megapdf_cli.cpp) is a thin shell over this one call.
 * ----------------------------------------------------------------------- */

/** megapdf_write_options.format. */
enum { MEGAPDF_WRITE_TEXT = 0, MEGAPDF_WRITE_MARKDOWN = 1 };

/** megapdf_write_options.page_break: how pages are separated in the output. */
typedef enum megapdf_page_break {
    MEGAPDF_PAGE_BREAK_FORM_FEED = 0,  /* U+000C between pages (default; pdftotext's own convention) */
    MEGAPDF_PAGE_BREAK_MARKER = 1,     /* "--- page N ---" lines (txt); "<!-- page N -->" for #357's Markdown */
    MEGAPDF_PAGE_BREAK_NONE = 2        /* no separator at all: a blank line, as between any two blocks */
} megapdf_page_break;

/** megapdf_write_options.fields: which FIELD blocks (contract 9) the writer includes. */
typedef enum megapdf_write_fields {
    MEGAPDF_WRITE_FIELDS_FILLED = 0,  /* default: a checked box, or a text field with a value (contract 9's own default) */
    MEGAPDF_WRITE_FIELDS_ALL = 1,     /* every field: empty text fields and unchecked boxes too (MEGAPDF_STRUCTURE_ALL_FIELDS) */
    MEGAPDF_WRITE_FIELDS_NONE = 2     /* no FIELD blocks: page text only */
} megapdf_write_fields;

/** Options for megapdf_write_text(). Zero-initialise and set what differs from the defaults. */
typedef struct megapdf_write_options {
    int keep_lines;       /* 1: keep the PDF's own line breaks inside a block; 0 (default): unwrap to one line */
    int page_break;       /* megapdf_page_break; 0 (MEGAPDF_PAGE_BREAK_FORM_FEED) is the default */
    int keep_furniture;   /* 1: running headers/footers/page numbers stay as blocks (MEGAPDF_STRUCTURE_KEEP_FURNITURE) */
    int fields;           /* megapdf_write_fields; 0 (MEGAPDF_WRITE_FIELDS_FILLED) is the default */
    int heuristic_only;   /* 1: MEGAPDF_STRUCTURE_HEURISTIC_ONLY -- ignore the structure tree even when present */
} megapdf_write_options;

/**
 * Writes the document's text over pages [first_page, first_page + page_count) through `write`,
 * in the shape megapdf_save() already uses (one abort-able callback, no file I/O in the core).
 * `format` is MEGAPDF_WRITE_TEXT or MEGAPDF_WRITE_MARKDOWN; `options` may be NULL for the
 * defaults above.
 *
 * Output is UTF-8, LF line endings, no BOM, built over contract 9's blocks (design §2/§3, the
 * 2026-09-24 comment on #142) and renders it, inferring nothing itself.
 *
 * MEGAPDF_WRITE_TEXT: one block per line by default (paragraphs unwrapped; a `continues`
 * paragraph is joined to the one before it with a space, not a break), a blank line between
 * blocks, headings bare, list items "marker text" indented two spaces per nesting level, fields
 * as "[x] name" / "[ ] name" (a checkbox or radio) or "name: value" (a text field), a page with
 * no text as one "[Page N has no text layer]" line, and U+000C between pages by default
 * (`page_break`).
 *
 * MEGAPDF_WRITE_MARKDOWN: CommonMark. HEADING → `#`×level + text (level capped at 6); PARAGRAPH/
 * FURNITURE → one unwrapped line, spans rendered as bold (`**`)/italic (`*`)/monospace (code
 * span), adjacent same-style spans merged, escaped per design §3 (`\`, `` ` ``, `*`, `_`, a
 * leading `#`/`>`, `[`/`]`, and a leading `-` or `\d+[.)]` that would otherwise start a list);
 * LIST_ITEM → "- text" for a glyph marker, "N. text" keeping the document's own ordinal for a
 * numeric marker, "1. <marker> text" for a lettered or roman marker (CommonMark has no lettered
 * lists), two spaces of indent per nesting level; FIELD → GitHub task-list syntax ("- [x] name" /
 * "- [ ] name") for a checkbox/radio, "**name:** value" for a text field; FIGURE → "*[Figure:
 * alt]*" only when alt text exists (tagged, #358), nothing otherwise; PAGE_IMAGE → "*[Page N has
 * no text layer]*"; TABLE_ROW (tagged only, #358) → a GitHub-flavoured pipe table per tagged
 * table (consecutive rows joined by `continues`): when every row has the same cell count, the
 * first row is the header when its cells were /TH (MEGAPDF_SPAN_CELL_HEADER) and an empty
 * header row is written otherwise, cells carry their span styling with `|` additionally
 * escaped; a table whose rows disagree on cell count is written one row per line, cells joined
 * by a tab. No page separator by default (a blank line, like between any two blocks) or with
 * MEGAPDF_PAGE_BREAK_NONE, `--page-marker` → "<!-- page N -->".
 *
 * In MEGAPDF_WRITE_TEXT a TABLE_ROW is its cells joined by a tab, and the rows of one table
 * follow each other on consecutive lines (no blank line between them); a FIGURE with alt text
 * is "[Figure: alt]" on its own line. In both formats the alt text is written as one line:
 * whitespace runs, line breaks included, collapse to a single space.
 *
 * `keep_lines` restores line breaks inside a block on a best-effort basis, for both formats:
 * contract 9 does not expose per-line boundaries (a span is a same-style run, which commonly
 * spans several visual lines), so a break is only recovered where two consecutive spans'
 * vertical centres do not overlap (BuildLines' own rule, reused); a plain, unstyled paragraph's
 * internal line breaks are not recoverable from the contract and stay unwrapped even with this
 * option. In Markdown, a recovered break always closes any open span styling first (a style
 * marker is never left open across a line) and is itself eligible for the line-start escapes.
 *
 * Returns the count of pages in the range that had a text layer (i.e. did not become a lone
 * PAGE_IMAGE block) — what megapdf-cli's exit code is chosen from — or a negative MEGAPDF_ERR_*:
 * MEGAPDF_ERR_ARGUMENT for a NULL document, a bad range, an unknown format or an unknown
 * `options` enum value; MEGAPDF_ERR_CANCELLED when `cancel` was raised before or during the
 * write (the #145 pattern every cancellable contract uses); MEGAPDF_ERR_PDFIUM when `write`
 * returns 0 (the #145/#110 abort convention megapdf_save() already uses).
 */
MEGAPDF_API int megapdf_write_text(megapdf_document* document, int first_page, int page_count, int format,
                                   const megapdf_write_options* options, megapdf_write_fn write, void* context,
                                   const megapdf_cancel* cancel);

/* --------------------------------------------------------------------------
 * Contract 10: page tools (#174). Rotate, delete, move, insert a blank page, import
 * pages from another file (combine) and extract pages to a new file (split). Every
 * page index here is 0-based, as megapdf_load_page's is.
 *
 * Permission: every call that changes the document needs MEGAPDF_PERMIT_ASSEMBLE or
 * MEGAPDF_PERMIT_MODIFY (ISO 32000-2 Table 22: "assemble the document — insert, rotate
 * or delete pages"); megapdf_pages_extract needs MEGAPDF_PERMIT_COPY. MEGAPDF_ERR_RESTRICTED
 * otherwise (ADR-004) — unless the app has called megapdf_security_override(), which is how
 * it says the person was told what the author asked and chose to continue anyway (#558,
 * ADR-004 decision 11). The *source* document of megapdf_pages_import still needs its own
 * MEGAPDF_PERMIT_COPY whatever this document was told. A document poisoned by a failed
 * redaction refuses them all with MEGAPDF_ERR_REDACT, as megapdf_save() does.
 *
 * Page indices are the identity key of everything the core caches per page and of every
 * entry the apps' recovery journals record (#145), and delete, move, restore, insert and
 * import all renumber pages. The core keeps its own per-page state right: every open
 * megapdf_page handle's index follows its page (megapdf_page_index()), and a handle whose
 * page was deleted answers -1 from then on and still renders (its widgets as drawn, no
 * longer live: the form calls on it answer MEGAPDF_ERR_ARGUMENT and it lists no fields),
 * so a view holding it does not crash; the redaction marks, layout verdicts and detached
 * objects of a page follow
 * it too, and those of a deleted page are dropped (a detached handle of a deleted page
 * can no longer be restored: MEGAPDF_ERR_ARGUMENT). What the core cannot see is the apps'
 * own index-keyed caches — render caches, thumbnail grids, the undo stack — which the app
 * renumbers itself after each call, exactly as it would after a page op made elsewhere.
 *
 * Undo and the journal. Every operation has an inverse in this contract, so the apps'
 * undo stacks and journals record page operations the way they record every other edit
 * — the effective stream, front to back, with each entry's page index as the document
 * was numbered when it was made — and a replay in order lands on the right pages
 * without any index rewriting: an entry recorded after a delete already carries the
 * post-delete index. The inverses:
 *   rotate(page, q)            ↔ rotate(page, -q)
 *   move(from, to)             ↔ move(to, from)
 *   insert_blank(at, w, h)     ↔ delete(at)
 *   import(n pages at at)      ↔ delete(at) n times
 *   delete(page)               ↔ restore(removed, page) in the session — the page kept
 *                                alive by its megapdf_removed_page handle, the way a
 *                                detached object is (contract 5), so an undo puts back
 *                                exactly what was deleted; a journal cannot carry a
 *                                page, so its entry for that undo is "import page N of
 *                                the file on disk at index page", best effort in the
 *                                sense TextRestoreEntry is: edits made to the page
 *                                before it was deleted are replayed by their own entries
 *                                only if they precede the delete.
 * megapdf_pages_extract changes nothing in the document and records nothing.
 *
 * Coordinates (#439): rotating a page sets its /Rotate and rewrites no content. Renders
 * follow the rotation (contract 7 renders through PDFium's display matrix, which honours
 * /Rotate), megapdf_page_width/height answer the rotated size, and so does crop space
 * itself: the rectangles every contract reports — form fields, stamps, check marks, text
 * runs and lines, search hits, redaction marks, page-object bounds, structure block
 * bounds — are in the rotated space the render draws, and every coordinate passed *in* is
 * read in that same space. There is **one** space and no flag to choose another: a caller
 * that draws a reported rect over a render needs no rotation term of its own, which is the
 * whole point, and a second space would only move this decision into four apps.
 *
 * So a page that arrives with /Rotate set, or that the user turns, reports rects a tap can
 * be tested against and a highlight can be drawn from, with no work on the binding's part.
 * Before #439 these rects were unrotated user space while the render was rotated, so a tap
 * landed in the wrong place on exactly those pages.
 *
 * Content the core *writes* onto a rotated page is turned with it (#446): the page's /Rotate
 * is composed into the object's own matrix, so an image stamp, a check mark, a text box and a
 * journalled text run are all drawn the way up they are seen by whoever is looking at the page
 * as its /Rotate says to show it. A stamp fills the rectangle it was given rather than a
 * turned one, and a text box reads along the line it was typed on.
 *
 * That is the choice, and the reason is what happens *later*. A page's /Rotate is set because
 * the page is meant to be seen that way — a landscape scan in a portrait MediaBox has its own
 * text drawn sideways in user space, and only reads upright because /Rotate turns it. Writing
 * new content turned puts it in the same frame as that content: a signature that sat upright
 * beside a paragraph still sits upright beside that paragraph at any later /Rotate, and
 * un-rotating the page lays both on their side together, which is what the un-rotated page
 * always looked like. Leaving the content axis-aligned in user space would instead put it in a
 * frame of its own, agreeing with the page at one rotation and disagreeing at the other three —
 * and the one where it agreed would be the rotation the document says *not* to show. Rewriting
 * /Rotate to 0 and turning the page's whole content was the third option, and is refused: it
 * rewrites content nobody edited.
 *
 * The one assumption this makes is the one /Rotate itself makes: that the page reads upright as
 * shown. A page whose content is drawn upright in user space *and* carries a non-zero /Rotate —
 * a document asking to be read sideways — gets new content upright-on-screen and therefore
 * turned against its own text. megapdf_insert_text_run inherits the same assumption when it
 * replays a journalled restore, because a journal entry carries a point and a font size, never
 * the run's original matrix; what it owes is the line as it was on screen, and that is what it
 * draws.
 *
 * megapdf_page_crop_origin() and megapdf_page_user_unit() report the two *unrotated* terms
 * of the transform, in user space, for a caller that needs the page's own geometry (a
 * saved-copy comparison, a diagnostic). They are not what a caller needs to map a reported
 * rect: those are already mapped.
 * ----------------------------------------------------------------------- */

/**
 * The page's index in its document as it is numbered now; -1 for a NULL handle or a deleted page
 * (a live answer a view acts on, #174), and MEGAPDF_ERR_CLOSED for a dead one (#551).
 */
MEGAPDF_API int megapdf_page_index(const megapdf_page* page);

/** The page's /Rotate in quarter turns clockwise, 0–3; MEGAPDF_ERR_ARGUMENT for a bad document or index. */
MEGAPDF_API int megapdf_page_rotation(const megapdf_document* document, int page);

/**
 * Rotates the page by `quarter_turns` quarter turns clockwise (negative for anticlockwise;
 * any magnitude, taken modulo 4): its /Rotate changes and nothing else does. Every open
 * handle on the page sees the new size and renders rotated. MEGAPDF_OK for 0 turns.
 */
MEGAPDF_API int megapdf_page_rotate(megapdf_document* document, int page, int quarter_turns);

/**
 * A deleted page kept alive for an undo. The core owns it: megapdf_page_restore() consumes
 * the handle, megapdf_discard_removed_page() frees it, and closing the document frees any
 * still held. A handle restores only into the document it came from.
 */
typedef struct megapdf_removed_page megapdf_removed_page;

/**
 * Deletes the page. With `out_removed` non-NULL the page is kept for megapdf_page_restore();
 * with it NULL the page is gone. Either way the page's form fields leave the document's
 * AcroForm with it (PDFium patch 0028), so a saved file does not carry a field whose only
 * widget was on a deleted page — nor the page, which such a field would have kept
 * reachable: a full save writes what the trailer reaches (SDD §3.4, patch 0029), so a
 * deleted page's objects are not written unless something else still points at them
 * (an outline entry or a link to the page, which the core leaves alone).
 *
 * MEGAPDF_ERR_ARGUMENT for a bad index or a document with one page (a PDF must have a
 * page); MEGAPDF_ERR_PDFIUM when the page could not be copied for the undo — the document
 * is then untouched.
 */
MEGAPDF_API int megapdf_page_delete(megapdf_document* document, int page, megapdf_removed_page** out_removed);

/**
 * Puts a deleted page back at index `at` (0 … page count, the count appends) and consumes
 * the handle. The page comes back as it was — content, resources, annotations and their
 * appearance streams, and its fields with their names and values, hierarchies included
 * (the copy is of the same document, so a widget's /Parent still names its field) — but
 * the fields are not re-registered in the AcroForm (PDFium's page import does not touch
 * it): PDFium, and so every MegaPDF platform, still lists, fills and draws them from the
 * widgets, and a save writes them; a reader that builds its form panel from /AcroForm
 * /Fields alone will not list them. MEGAPDF_ERR_ARGUMENT for a NULL handle, a handle from
 * another document or a bad index; MEGAPDF_ERR_PDFIUM when PDFium refuses (the handle
 * stays valid); MEGAPDF_ERR_CLOSED when the handle's own document has been closed, answered
 * before "another document" because it is the more specific truth (#551).
 */
MEGAPDF_API int megapdf_page_restore(megapdf_document* document, megapdf_removed_page* removed, int at);

/**
 * Frees a removed page without restoring it. NULL is fine, and so is a handle whose document
 * has already been closed — closing the document frees the page copy it held and leaves the
 * handle valid for exactly this call (#551).
 */
MEGAPDF_API void megapdf_discard_removed_page(megapdf_removed_page* removed);

/**
 * Moves the page at `from` so that it stands at index `to` afterwards (the indices of the
 * pages between them shift by one). The page dictionary is untouched, so its fields,
 * annotations and everything else move with it. MEGAPDF_OK when from == to.
 */
MEGAPDF_API int megapdf_page_move(megapdf_document* document, int from, int to);

/** Inserts an empty page of `width` × `height` points at `at` (0 … page count, the count appends). */
MEGAPDF_API int megapdf_page_insert_blank(megapdf_document* document, int at, double width, double height);

/**
 * Combine: inserts pages of the file at `other_path_utf8` before index `insert_at` (0 … page
 * count, the count appends), in the order `pages` lists them (0-based indices into the other
 * document; NULL with `count` 0 means all of its pages). `password_utf8` opens the other
 * file when it needs one (NULL otherwise). `out_imported` (may be NULL) receives how many
 * pages were inserted.
 *
 * The other file is read on demand (megapdf_open_file, #147): only the pages imported are
 * copied, with their resources — the fonts, images and forms they draw come with them, and
 * a resource two imported pages share is copied once. Form fields on the imported pages
 * keep their names unless a top-level name already exists in this document, in which case
 * the imported field is renamed with a numeric suffix ("name" → "name_2") before it is
 * copied, so the two never merge into one field. As for megapdf_page_restore(), the
 * imported fields are not registered in this document's AcroForm.
 *
 * A form field in a hierarchy — a widget whose name, type or value lives on a /Parent
 * field dictionary, as LiveCycle and most authoring tools write them — used to arrive
 * without its name or type: PDFium's page copy left the widget's /Parent pointing into
 * the other document, and the saved file named an object that was not its parent. Such
 * pages are refused whole, with MEGAPDF_ERR_FIELDS, and nothing is changed.
 *
 * Fixed by a PDFium patch (#452, tools/pdfium/patches/0033): the /Parent chain and the
 * field dictionaries it names are copied too, pruned to the part of the hierarchy that
 * came across (a sibling widget on a page not imported is dropped from its parent's
 * /Kids, the way megapdf_page_delete drops a field left with no widgets), and the
 * chain's root is registered in this document's /AcroForm — the one case where an
 * import *does* touch the destination's AcroForm registration, forced by the copy
 * itself rather than chosen here. The rename above still can't reach such a field,
 * though: it renames a clashing widget's own /T, and a hierarchical widget has none
 * (its name is its parent's). A hierarchy whose top-level name would clash is
 * therefore still refused whole, with MEGAPDF_ERR_FIELDS, and nothing is changed; one
 * that does not clash is imported and keeps its name.
 *
 * Whether this document is built against a PDFium old enough to need the whole-page
 * refusal, or new enough to need only the narrower clash refusal, is fixed at compile
 * time (MEGAPDF_PDFIUM_PATCHES; core/CMakeLists.txt and
 * android/engine/src/main/cpp/CMakeLists.txt both read it from the linked PDFium's own
 * VERSION file) — not something a caller can ask for or detect except by trying the
 * call, and not something this file can safely tell at runtime either: a save-and-reopen
 * probe was tried and rejected (see git history around #452) because PDFium's own
 * orphan-widget recovery can mask the very bug such a probe looks for. A widget that is
 * its own field (no /Parent), which is what simple forms and MegaPDF's fixtures mostly
 * have, imports and renames exactly as always, on any PDFium. A popup annotation's
 * /Parent has the same shape and needs no equivalent fix, on any PDFium: PDFium reads
 * the markup annotation's /Popup, never the popup's /Parent, and a reader that does sees
 * a link to the wrong object rather than a lost note.
 *
 * The other document stays open inside this one until it is closed, so nothing an imported
 * page still refers to in it can be freed under the document. One PDFium call copies every
 * page, so there is no cancel flag: importing page by page would copy a shared resource
 * once per page.
 *
 * MEGAPDF_ERR_FILE when the other file cannot be opened or is not a PDF, MEGAPDF_ERR_RESTRICTED
 * when it needs a password or its own security does not allow copying from it (COPY, as
 * megapdf_pages_extract), MEGAPDF_ERR_ARGUMENT for a bad index in `pages` or `insert_at`,
 * MEGAPDF_ERR_FIELDS as above, MEGAPDF_ERR_PDFIUM when PDFium refuses the copy.
 * megapdf_last_error() carries PDFium's open code (FPDF_ERR_PASSWORD, FPDF_ERR_FORMAT, ...)
 * after a failed open, as megapdf_open() does.
 */
MEGAPDF_API int megapdf_pages_import(megapdf_document* document, const char* other_path_utf8,
                                     const char* password_utf8, const int* pages, size_t count, int insert_at,
                                     int* out_imported);

/**
 * Split: writes the listed pages (0-based, in the order given, repeats allowed; NULL with
 * `count` 0 means every page) as a new PDF at `out_path_utf8`, with the save discipline
 * every platform's save uses (SDD §3.4): the whole file goes to a sibling temporary name
 * in the destination's directory, is opened again and its page count checked, and only
 * then takes the destination's name, so a crash or a full disk leaves either the old file
 * or the new one, never a torn one. Form edits are committed first, as for megapdf_save().
 *
 * The new file carries no security (a copy the user may make of a document whose security
 * permits copying), no outline and, for a field that is its own widget, no document-level
 * form dictionary: such a widget comes across with its appearance stream and is not
 * fillable in a reader that needs /AcroForm — extract does not build one.
 *
 * Pages with fields in a /Parent hierarchy are refused whole with MEGAPDF_ERR_FIELDS, for
 * the reason megapdf_pages_import() gives, on a PDFium old enough that its page copy
 * cannot carry the hierarchy (#174). On one new enough (#452, MEGAPDF_PDFIUM_PATCHES —
 * see megapdf_pages_import() for what decides this and why it is fixed at compile time,
 * not asked for), such a field is different from a flat one only because it has to be:
 * copying the hierarchy at all means copying its root into *some* /AcroForm /Fields
 * array, so the new file gets a minimal one, carrying just the hierarchies extract
 * copied, the moment the first such page is extracted; it is fillable where a
 * same-shaped flat field, on the same extract, is not. There is no name to clash with in
 * a brand new file, so on that PDFium this never refuses for MEGAPDF_ERR_FIELDS at all.
 * The document itself is unchanged.
 *
 * `cancel` may be NULL; raised, it stops the write and returns MEGAPDF_ERR_CANCELLED with
 * nothing left at `out_path_utf8`. MEGAPDF_ERR_ARGUMENT for a bad index or an empty path,
 * MEGAPDF_ERR_FILE when the file cannot be written, read back or renamed into place,
 * MEGAPDF_ERR_PDFIUM when PDFium refuses.
 */
MEGAPDF_API int megapdf_pages_extract(const megapdf_document* document, const int* pages, size_t count,
                                      const char* out_path_utf8, const megapdf_cancel* cancel);

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif /* MEGAPDF_CORE_H */
