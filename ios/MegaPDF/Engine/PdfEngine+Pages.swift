import CoreGraphics
import CPdfium
import Foundation

// Contract 10: the page tools (#174) — the Swift face of `megapdf_page_rotate`, `_delete`,
// `_restore`, `_move`, `_insert_blank`, `megapdf_pages_import` and `megapdf_pages_extract`.
//
// An extension file beside `PdfEngine.swift` rather than more of it, as every other contract
// here is (`PdfEngine+Redaction.swift`, `+BodyText.swift`, …): one contract, one file. They
// run on the engine actor like every other call, because PDFium is not thread-safe.
//
// **Every refusal is named.** These are the calls on this boundary that can be refused for
// several genuinely different reasons, and the difference is the whole of what the app owes
// the person: "this document does not allow its pages to be moved about" is not "those pages
// carry a form field that cannot be copied" is not "the file could not be written". So a
// refusal arrives as `PageToolError` carrying a `PageToolRefusal`, and the app turns that
// into a sentence in the user's language. Nothing here is retried, softened or hidden: when
// the core refuses, the document is untouched, and saying so plainly is the feature.

/// Why a page tool refused. Each one has its own sentence in `ViewerModel.sentence(for:)`.
enum PageToolRefusal: Equatable {
    /// This document's security does not allow it: `MEGAPDF_ERR_RESTRICTED` from the
    /// assemble / modify check (ISO 32000-2 Table 22, "assemble the document — insert,
    /// rotate or delete pages"), or from the copy check an extract needs. Its owner
    /// password would allow it.
    case restricted

    /// The *other* file needs a password, so no pages can be taken from it.
    case sourceNeedsPassword

    /// The other file's own security does not allow copying out of it.
    case sourceRestricted

    /// `MEGAPDF_ERR_FIELDS`: a page's form fields sit in a /Parent hierarchy whose top-level
    /// name this document already uses, so the copy would have to rename a name that lives on
    /// the parent rather than on the widget. Refused **whole**, because the alternative is a
    /// document whose fields quietly lost their names and values.
    ///
    /// **Common, not exotic.** The corpus battery's ninth run (#567) measured it at **0.91% of
    /// the private corpus and 15.4% of the public one** — the latter being government-forms
    /// heavy, which is exactly the population that writes fields this way — plus **3.0%** of
    /// the Canadian corpus from genuine collisions *between* two documents. Earlier runs
    /// reported zero, and that zero turned out to be the absence of a probe rather than the
    /// absence of the problem. So this reaches a person often enough that its sentence is a
    /// feature of the app rather than a corner of it, and it is said in a titled alert rather
    /// than a notice that clears itself.
    ///
    /// **Only an import can raise it.** An extract writes a brand-new file, and on this build's
    /// PDFium (patch ≥ 33, which copies the /Parent chain) there is no name in a new file for a
    /// hierarchy to collide with — so `megapdf_pages_extract` can no longer answer it at all.
    /// The mapping below still covers extract, because the core documents the status for an
    /// older PDFium and a status that cannot arrive costs nothing to name.
    case fieldHierarchy

    /// A PDF must keep at least one page, so the last one may not be deleted.
    case lastPage

    /// The file could not be opened, read, written or renamed into place.
    case file

    /// A redaction failed part-way, so the document may only be closed (#173).
    case redactionPoisoned

    /// A removed-page handle that has already been restored or discarded (#429/#441).
    ///
    /// Not a state a person can reach: it is a broken undo history, and the only reason it
    /// has a case of its own is that it must fail **loudly** rather than quietly do nothing
    /// — which is exactly the shape of the two bugs that made an Undo press take back the
    /// wrong edit, twice. It borrows `engine`'s sentence, because there is nothing useful to
    /// say to a person about it.
    case spentPage

    /// PDFium refused, or the core could not allocate. Nothing was changed.
    case engine
}

/// A page tool refused, with `refusal` saying which refusal it was and `status` the core's
/// own `MEGAPDF_ERR_*` code behind it. The document is as it was: contract 10's calls change
/// either everything they promised or nothing.
struct PageToolError: Error, Equatable {
    let refusal: PageToolRefusal
    let status: Int

    init(_ refusal: PageToolRefusal, status: Int = Int(MEGAPDF_ERR_PDFIUM)) {
        self.refusal = refusal
        self.status = status
    }
}

/// A deleted page the core is holding so an undo can put it back — the page itself, not a
/// description of it, so what comes back is exactly what went away (its content, annotations,
/// appearance streams and its fields with their names and values).
///
/// The core owns it: `PdfEngine.restorePage` consumes the handle, `PdfEngine
/// .discardRemovedPage` frees it, and closing the document frees any still held. A handle
/// restores only into the document it came from, which is why the document is held here —
/// and held *strongly*, the way `PdfDocument` itself is: a discard has to be able to ask
/// whether the document has already been closed (#549), and a page whose document went with
/// `megapdf_close` must not be freed a second time.
///
/// `@unchecked Sendable` on the same terms as `PdfDocument`: `handle` is only ever read or
/// written on the engine actor. `isHeld` is the one thing the app reads, and it reads it to
/// assert on, never to decide with.
final class RemovedPage: @unchecked Sendable {
    let owner: PdfDocument
    fileprivate var handle: OpaquePointer?

    fileprivate init(owner: PdfDocument, handle: OpaquePointer) {
        self.owner = owner
        self.handle = handle
    }

    /// False once this page has been restored or discarded.
    var isHeld: Bool { handle != nil }
}

/// Which call is being made, for the two status codes that mean different things depending.
private enum PageOp {
    case rotate, delete, restore, move, insert, importPages, extract
}

extension PdfEngine {

    // MARK: - reading

    /// The page's `/Rotate` in quarter turns clockwise, 0–3.
    func pageRotation(_ document: PdfDocument, pageIndex: Int) throws -> Int {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        return try checked(Int(megapdf_page_rotation(document.core, Int32(pageIndex))), .rotate)
    }

    // MARK: - the five reversible operations

    /// Turns the page by `quarterTurns` quarter turns clockwise (negative anticlockwise): its
    /// `/Rotate` changes and nothing else does — no content is rewritten, so this is **not**
    /// the kind of edit PDFium's writer can decline (#118 / contract 10). Every open handle on
    /// the page sees the new size, and the rectangles every other contract reports are in the
    /// rotated space the render draws (#439), so a tap and a highlight follow the page round
    /// with no work on this side.
    func rotatePage(_ document: PdfDocument, pageIndex: Int, quarterTurns: Int) throws {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        _ = try checked(Int(megapdf_page_rotate(document.core, Int32(pageIndex), Int32(quarterTurns))), .rotate)
    }

    /// Deletes the page and hands back the copy the core kept for an undo. The page's form
    /// fields leave the document's AcroForm with it, so a saved file carries neither a field
    /// whose only widget was on a deleted page nor the page such a field would have kept
    /// reachable.
    func deletePage(_ document: PdfDocument, pageIndex: Int) throws -> RemovedPage {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        var removed: OpaquePointer?
        let status = Int(megapdf_page_delete(document.core, Int32(pageIndex), &removed))
        _ = try checked(status, .delete)
        guard let removed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_MEMORY)) }
        return RemovedPage(owner: document, handle: removed)
    }

    /// Deletes the page for good, keeping nothing for an undo — for an undo that is itself
    /// undoing an insert or an import, where a redo makes the page again from the two numbers
    /// or the file it came from.
    func deletePageForGood(_ document: PdfDocument, pageIndex: Int) throws {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        var removed: OpaquePointer?
        let status = Int(megapdf_page_delete(document.core, Int32(pageIndex), &removed))
        if let removed { megapdf_discard_removed_page(removed) }
        _ = try checked(status, .delete)
    }

    /// Puts a deleted page back at `index` (0 … page count, the count appends) and consumes
    /// the handle.
    ///
    /// **A spent handle throws** (#429/#441). Both of those bugs were a history holding an
    /// identifier the engine had replaced, where the call quietly answered "no" and the Undo
    /// the person pressed did nothing — leaving the history one step out, so the *next* press
    /// took back something else. A page operation that names a page the core no longer has is
    /// a broken history, and it fails the way one does: the operation goes back on the stack
    /// and the screen says the change could not be made.
    ///
    /// A restore PDFium refuses (`MEGAPDF_ERR_PDFIUM`) leaves the handle valid, so it is not
    /// consumed — the undo can be pressed again.
    func restorePage(_ document: PdfDocument, _ removed: RemovedPage, at index: Int) throws {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        guard removed.owner === document else {
            throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT))
        }
        guard let handle = removed.handle else {
            throw PageToolError(.spentPage, status: Int(MEGAPDF_ERR_ARGUMENT))
        }
        _ = try checked(Int(megapdf_page_restore(document.core, handle, Int32(index))), .restore)
        removed.handle = nil   // the core consumed it
    }

    /// Frees a removed page without putting it back: the undo that would have used it is gone.
    ///
    /// Under the old contract, a page whose document had already closed went with it —
    /// `megapdf_close()` deleted every removed page it still held — so this used to do nothing
    /// then, at the cost of leaking the ~16-byte shell `megapdf_close()` now empties instead of
    /// deleting (#578's dead-handle contract). `megapdf_discard_removed_page` accepts a dead
    /// handle on purpose and only frees that shell, so the call below is made whether or not
    /// the document has closed meanwhile, and the leak is gone (#583). This guard, added for
    /// page tools (#174), was not among the sites #578's report named — it only enumerated the
    /// Android and .NET guards, going by "iOS scopes its page handles rather than guarding" —
    /// but it skips the same free the same way, so it needed the same fix.
    ///
    /// Freeing it twice is still a use after free regardless of the document's state, and
    /// teardown order is not something the caller of a discard is thinking about, so the
    /// `removed.handle` guard above — distinct from the one just removed — stays: it is what
    /// makes this safe to call twice, and safe to call on a handle a restore has consumed.
    func discardRemovedPage(_ removed: RemovedPage) {
        guard let handle = removed.handle else { return }
        removed.handle = nil
        megapdf_discard_removed_page(handle)
    }

    /// Moves the page at `from` so it stands at `to` afterwards; the pages between shift by
    /// one. The page dictionary is untouched, so its fields, annotations, marks and everything
    /// else travel with it.
    func movePage(_ document: PdfDocument, from: Int, to: Int) throws {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        _ = try checked(Int(megapdf_page_move(document.core, Int32(from), Int32(to))), .move)
    }

    /// Inserts an empty page of `widthPoints` × `heightPoints` at `index` (0 … page count,
    /// the count appends).
    func insertBlankPage(_ document: PdfDocument, at index: Int,
                         widthPoints: Double, heightPoints: Double) throws {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        _ = try checked(Int(megapdf_page_insert_blank(document.core, Int32(index),
                                                      widthPoints, heightPoints)), .insert)
    }

    /// Combine: inserts the pages of the PDF at `path` before `insertAt`, in the order `pages`
    /// lists them (nil means all of them), and answers how many arrived. Only the pages asked
    /// for are copied, with the fonts, images and forms they draw; a field whose name is
    /// already taken here is renamed with a numeric suffix so the two never merge into one
    /// field.
    ///
    /// The other document stays open inside this one until this one closes, so `path` must
    /// stay readable for that long — which is why the app imports from a copy of the picked
    /// file inside its own container and keeps it until the document is closed, rather than
    /// from a URL whose security-scoped access ends with the picker.
    ///
    /// Throws `PageToolRefusal.fieldHierarchy` when a page's fields sit in a /Parent hierarchy
    /// whose top-level name is already taken here: the copy would have to rename a name that
    /// lives on the parent rather than on the widget, so the pages are refused whole and
    /// nothing is changed.
    func importPages(_ document: PdfDocument, from path: String, password: String? = nil,
                     pages: [Int]? = nil, insertAt: Int) throws -> Int {
        guard !document.isDestroyed else { throw PageToolError(.engine, status: Int(MEGAPDF_ERR_ARGUMENT)) }
        var imported: Int32 = 0
        let indices = (pages?.isEmpty == false) ? pages!.map { Int32($0) } : nil
        let status: Int = path.withCString { otherPath in
            func run(_ pw: UnsafePointer<CChar>?) -> Int {
                guard let indices else {
                    return Int(megapdf_pages_import(document.core, otherPath, pw, nil, 0,
                                                    Int32(insertAt), &imported))
                }
                return indices.withUnsafeBufferPointer {
                    Int(megapdf_pages_import(document.core, otherPath, pw, $0.baseAddress,
                                             $0.count, Int32(insertAt), &imported))
                }
            }
            if let password { return password.withCString { run($0) } }
            return run(nil)
        }
        _ = try checked(status, .importPages)
        return Int(imported)
    }

    // MARK: - extract (a copy, not an edit)

    /// Split: writes `pages` (nil means every page, in the order given) as a new PDF at `url`,
    /// with the save discipline every platform's save uses — the whole file to a sibling
    /// temporary name, reopened and its page count checked, and only then given the
    /// destination's name, so a crash or a full disk leaves either no file or a whole one.
    /// This document is unchanged and nothing is recorded: an extract is a copy, not an edit.
    func extractPages(_ document: PdfDocument, pages: [Int]?, to url: URL) throws {
        _ = try checked(extractPages(document, indices: pages, to: url), .extract)
    }

    /// Writes `indices` (nil/empty means every page) as a new PDF at `url`, exactly as
    /// `megapdf_pages_extract` does (core/megapdf_core.h) -- including MEGAPDF_ERR_FIELDS
    /// for a page whose form fields sit in a /Parent hierarchy this build's PDFium cannot
    /// carry across a page copy. Returns the raw core status code (MEGAPDF_OK and friends,
    /// imported as plain `Int`) rather than throwing, since that refusal is exactly what
    /// `PagesFieldHierarchyTests` needs to see (#469).
    func extractPages(_ document: PdfDocument, indices: [Int]? = nil, to url: URL) -> Int {
        guard !document.isDestroyed else { return Int(MEGAPDF_ERR_ARGUMENT) }
        return url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return Int(MEGAPDF_ERR_ARGUMENT) }
            guard let indices, !indices.isEmpty else {
                return Int(megapdf_pages_extract(document.core, nil, 0, path, nil))
            }
            let pages = indices.map { Int32($0) }
            return pages.withUnsafeBufferPointer {
                Int(megapdf_pages_extract(document.core, $0.baseAddress, $0.count, path, nil))
            }
        }
    }

    /// The fully qualified name of every form field on the page (`MEGAPDF_FIELD_NAME`,
    /// core/megapdf_core.h contract 3) -- the minimal read `PagesFieldHierarchyTests` needs
    /// to prove an extracted hierarchical field kept its name, not a general-purpose API.
    func fieldNames(_ document: PdfDocument, pageIndex: Int) throws -> [String] {
        try withCorePage(document, index: pageIndex) { page in
            guard let fields = megapdf_form_fields_load(page) else { return [] }
            defer { megapdf_form_fields_free(fields) }
            var names: [String] = []
            for i in 0..<megapdf_form_field_count(fields) {
                var buffer = [UInt16](repeating: 0, count: 256)
                let count = buffer.withUnsafeMutableBufferPointer {
                    megapdf_form_field_string(fields, i, MEGAPDF_FIELD_NAME, $0.baseAddress, $0.count)
                }
                names.append(String(decoding: buffer.prefix(count), as: UTF16.self))
            }
            return names
        }
    }

    // MARK: - contract evidence

    /// What a page operation does to the document's numbering, for a page handle held across
    /// it (contract 10: "every open `megapdf_page` handle's index follows its page, and a
    /// handle whose page was deleted answers -1 from then on and still renders").
    enum PageChange: Equatable {
        case move(from: Int, to: Int)
        case delete(Int)
        case insertBlank(at: Int)
    }

    /// Opens a page handle at `index`, applies `change`, and answers what
    /// `megapdf_page_index()` says about that same handle afterwards.
    ///
    /// The app itself holds no page handle across a page operation — every read on this
    /// platform opens a page, uses it and closes it inside one actor call
    /// (`withCorePage`), and the renumbering of the app's own index-keyed state is
    /// `PageShift`'s job, not a handle's. So this exists for one purpose: to hold the core to
    /// the half of the contract that would otherwise be taken on trust, from a test. It is on
    /// the engine because the handle has to outlive a call, and an actor cannot lend one out.
    func pageIndexFollowing(_ document: PdfDocument, openedAt index: Int,
                            applying change: PageChange) throws -> Int {
        guard !document.isDestroyed, let page = megapdf_load_page(document.core, Int32(index)) else {
            throw PdfError.pageLoad(index: index)
        }
        defer { megapdf_close_page(page) }
        switch change {
        case let .move(from, to):
            try movePage(document, from: from, to: to)
        case let .delete(target):
            try deletePageForGood(document, pageIndex: target)
        case let .insertBlank(at):
            try insertBlankPage(document, at: at, widthPoints: 612, heightPoints: 792)
        }
        return Int(megapdf_page_index(page))
    }

    // MARK: - internals

    /// The core's status, or the named refusal behind it.
    ///
    /// Two codes carry more than one meaning, and both are resolved here rather than in the
    /// app:
    ///
    ///  * `MEGAPDF_ERR_ARGUMENT` from a delete is the one-page rule — every index the app
    ///    passes comes from a page count it has just read, so "no page at that index" is not
    ///    reachable from the UI while "a document must keep at least one page" is exactly
    ///    what a person can ask for.
    ///  * `MEGAPDF_ERR_RESTRICTED` from an import is the *other* file's security, not this
    ///    document's: the app checks its own document's assemble permission before it offers
    ///    the command at all (`DocumentCapabilities.canAssemblePages`), so what is left is a
    ///    file that needs a password (`FPDF_ERR_PASSWORD`) or one that forbids copying out of
    ///    it.
    @discardableResult
    private func checked(_ status: Int, _ op: PageOp) throws -> Int {
        if status >= 0 { return status }
        // `if` / `else if` rather than a `switch`: the core's status codes come out of an
        // anonymous C enum, so their Swift type is not the `Int32` the calls themselves return,
        // and a `case` pattern cannot bridge the two. `Int(_:)` reads either.
        let refusal: PageToolRefusal
        if status == Int(MEGAPDF_ERR_FIELDS) {
            refusal = .fieldHierarchy
        } else if status == Int(MEGAPDF_ERR_REDACT) {
            refusal = .redactionPoisoned
        } else if status == Int(MEGAPDF_ERR_FILE) {
            refusal = .file
        } else if status == Int(MEGAPDF_ERR_RESTRICTED) {
            if op != .importPages {
                refusal = .restricted
            } else if Int(megapdf_last_error()) == FPDF_ERR_PASSWORD {
                refusal = .sourceNeedsPassword
            } else {
                refusal = .sourceRestricted
            }
        } else if status == Int(MEGAPDF_ERR_ARGUMENT) {
            refusal = op == .delete ? .lastPage : .engine
        } else {
            refusal = .engine
        }
        throw PageToolError(refusal, status: status)
    }
}
