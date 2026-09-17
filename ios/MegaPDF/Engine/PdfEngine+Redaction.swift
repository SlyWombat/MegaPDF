import CPdfium
import Foundation

// Redaction (#173, SDD §3.8 / F7, §6.2 contract 5).
//
// A whiteout covers; a redaction removes. iOS has no whiteout — mobile's feature set is
// fill, check, sign, find and add text — so Redact arrives here on its own, and its label
// says what it does.
//
// A mark is the core's own: it is never a page object, and it is never written to the file.
// A document saved with marks on it therefore carries none, which is the failure this
// feature exists to stop — a file that *looks* redacted, carrying both the content and a
// set of rectangles pointing at where the interesting content is — made impossible by the
// design rather than left to a rule every screen has to remember. So the view draws marks
// itself, and marking costs no content regeneration.

/// An area marked for redaction. `rect` is crop space (#30), like every rectangle here.
struct PdfRedactionMark: Equatable, Identifiable {
    let markId: Int
    let rect: PdfRect

    var id: Int { markId }
}

/// Why `applyRedactions` refused.
enum PdfRedactionRefusalReason: Int32 {
    /// A partly covered run in a Type 3 font, whose glyphs are content streams.
    case type3Font = 1
    /// The glyphs outside the area cannot be drawn back in the run's own font.
    case fontCannotRedraw = 2
    /// The guard saw something outside the area move or change (#118, #128).
    case layoutGuard = 3
    /// A form XObject reaches into the area; PDFium cannot write back an edit made inside one.
    case formXObject = 4
    /// An image's stored pixels could not be read or written back.
    case image = 5
    /// An annotation or form field could not be removed with its value.
    case annotation = 6
    /// The engine refused a step.
    case engine = 7
    /// A reason this build does not know about.
    case unknown = 0

    init(code: Int32) {
        self = PdfRedactionRefusalReason(rawValue: code) ?? .unknown
    }
}

/// One page the redaction would not touch, and why.
struct PdfRedactionRefusal: Equatable {
    let pageIndex: Int
    let reason: PdfRedactionRefusalReason
}

/// What a completed redaction removed, for the summary shown after saving.
struct PdfRedactionCounts: Equatable {
    var areas = 0
    var pages = 0
    var characters = 0
    var textRuns = 0
    var partialRuns = 0
    var hiddenCopies = 0
    var images = 0
    var inlineImages = 0
    var softMasks = 0
    var paths = 0
    var shadings = 0
    var formXObjects = 0
    var annotations = 0
    var formFields = 0
    var links = 0
    var outlineEntries = 0
    var structureEntries = 0
    var pageLabels = 0
    var metadataFields = 0
    var attachments = 0
}

/// The result of applying every mark. `applied` false means NOTHING was removed.
struct PdfRedactionReport: Equatable {
    let applied: Bool
    let counts: PdfRedactionCounts
    let refusals: [PdfRedactionRefusal]
    /// True when the document lacks the modify permission (ADR-004 decision 2).
    let needsPermission: Bool
}

extension PdfEngine {

    /// Marks an area for redaction and returns the mark's id. Nothing on the page changes.
    @discardableResult
    func markForRedaction(_ document: PdfDocument, pageIndex: Int, rect: PdfRect) throws -> Int {
        try withCorePage(document, index: pageIndex) { page in
            var area = megapdf_rect(left: rect.left, bottom: rect.bottom,
                                    right: rect.right, top: rect.top)
            var markId: Int32 = -1
            guard megapdf_redaction_mark(page, &area, &markId) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
            return Int(markId)
        }
    }

    /// Marks the text a drag selected: one mark per line it spans, each grown to the glyphs
    /// it touches, so a mark always covers whole glyphs. Returns how many were made; 0 means
    /// the selection covers no text, and the caller then marks the rectangle itself.
    ///
    /// Not count-then-fill, unlike everything else here: this call MAKES the marks, so
    /// asking it for a size first would make them twice.
    @discardableResult
    func markTextForRedaction(_ document: PdfDocument, pageIndex: Int, rect: PdfRect) throws -> Int {
        try withCorePage(document, index: pageIndex) { page in
            var selection = megapdf_rect(left: rect.left, bottom: rect.bottom,
                                         right: rect.right, top: rect.top)
            return Int(megapdf_redaction_mark_text(page, &selection, nil, 0))
        }
    }

    /// The marks on a page, in the order they were made.
    func redactionMarks(_ document: PdfDocument, pageIndex: Int) throws -> [PdfRedactionMark] {
        try withCorePage(document, index: pageIndex) { page in
            let count = megapdf_redaction_marks(page, nil, 0)
            guard count > 0 else { return [] }
            var buffer = [megapdf_redaction_area](repeating: megapdf_redaction_area(), count: count)
            let filled = megapdf_redaction_marks(page, &buffer, count)
            return buffer.prefix(filled).map {
                PdfRedactionMark(markId: Int($0.mark_id),
                                 rect: PdfRect(left: $0.bounds.left, bottom: $0.bounds.bottom,
                                               right: $0.bounds.right, top: $0.bounds.top))
            }
        }
    }

    /// Removes a mark. Already gone counts as success, so an undo cannot fail.
    func removeRedactionMark(_ document: PdfDocument, pageIndex: Int, markId: Int) throws {
        try withCorePage(document, index: pageIndex) { page in
            megapdf_redaction_remove_mark(page, Int32(markId))
        }
    }

    /// How many areas are marked across the document: what the save confirmation asks.
    func redactionMarkCount(_ document: PdfDocument) -> Int {
        guard !document.isDestroyed else { return 0 }
        return Int(megapdf_redaction_mark_count(document.core))
    }

    func clearRedactionMarks(_ document: PdfDocument) {
        guard !document.isDestroyed else { return }
        megapdf_redaction_clear(document.core)
    }

    /// True when a redaction failed part-way and the document may no longer be saved.
    func isRedactionPoisoned(_ document: PdfDocument) -> Bool {
        guard !document.isDestroyed else { return false }
        return megapdf_redaction_poisoned(document.core) == 1
    }

    /// Applies every mark and drops them. It fails closed: when `applied` is false NOTHING
    /// was removed, the document is exactly as it was, the marks are still on it, and
    /// `refusals` says which page and why.
    ///
    /// Applying also frees every undo handle the core holds — those keep removed objects
    /// alive so an undo can put them back, which after a redaction is the one thing that
    /// must not happen — so the caller clears its own history in the same step.
    func applyRedactions(_ document: PdfDocument) -> PdfRedactionReport {
        guard !document.isDestroyed else {
            return PdfRedactionReport(applied: false, counts: PdfRedactionCounts(),
                                      refusals: [], needsPermission: false)
        }
        return {
            let core = document.core
            var report: OpaquePointer?
            let status = megapdf_redact_apply(core, nil, &report)
            defer { if let report { megapdf_redaction_report_free(report) } }

            var counts = PdfRedactionCounts()
            var refusals: [PdfRedactionRefusal] = []
            if let report {
                var raw = megapdf_redaction_counts()
                if megapdf_redaction_report_counts(report, &raw) == MEGAPDF_OK {
                    counts = PdfRedactionCounts(
                        areas: Int(raw.areas), pages: Int(raw.pages),
                        characters: Int(raw.characters), textRuns: Int(raw.text_runs),
                        partialRuns: Int(raw.partial_runs), hiddenCopies: Int(raw.hidden_copies),
                        images: Int(raw.images), inlineImages: Int(raw.inline_images),
                        softMasks: Int(raw.soft_masks), paths: Int(raw.paths),
                        shadings: Int(raw.shadings), formXObjects: Int(raw.form_xobjects),
                        annotations: Int(raw.annotations), formFields: Int(raw.form_fields),
                        links: Int(raw.links), outlineEntries: Int(raw.outline_entries),
                        structureEntries: Int(raw.structure_entries), pageLabels: Int(raw.page_labels),
                        metadataFields: Int(raw.metadata_fields), attachments: Int(raw.attachments))
                }
                let count = megapdf_redaction_refusals(report, nil, 0)
                if count > 0 {
                    var buffer = [megapdf_redaction_refusal](
                        repeating: megapdf_redaction_refusal(), count: count)
                    let filled = megapdf_redaction_refusals(report, &buffer, count)
                    refusals = buffer.prefix(filled).map {
                        PdfRedactionRefusal(pageIndex: Int($0.page_index),
                                            reason: PdfRedactionRefusalReason(code: $0.reason))
                    }
                }
            }
            return PdfRedactionReport(applied: status == MEGAPDF_OK, counts: counts,
                                      refusals: refusals, needsPermission: status == MEGAPDF_ERR_RESTRICTED)
        }()
    }
}

/// What the app says after a redaction: "3 areas redacted: 41 characters, 1 image". The
/// logic is here and the words come from the caller, so every platform counts the same
/// things in the same order.
enum PdfRedactionSummary {
    enum Kind { case characters, images, formFields, annotations }

    static func removed(_ counts: PdfRedactionCounts,
                        plural: (Kind, Int) -> String,
                        singular: (Kind) -> String,
                        nothing: String) -> String {
        var parts: [String] = []
        func add(_ kind: Kind, _ n: Int) {
            guard n > 0 else { return }
            parts.append(n == 1 ? singular(kind) : plural(kind, n))
        }
        // Characters first: it is what people mark, and what they want to hear went.
        add(.characters, counts.characters)
        add(.images, counts.images)
        add(.formFields, counts.formFields)
        // Annotations less the fields already counted: a widget is both, and saying so
        // twice would read as more than was removed.
        add(.annotations, max(0, counts.annotations - counts.formFields))
        return parts.isEmpty ? nothing : parts.joined(separator: ", ")
    }
}
