import CoreGraphics
import Foundation
import UIKit

/// Where a document is opened from (#147): its file, read on demand, or bytes already in
/// memory (the demo and UI-test documents).
enum DocumentSource {
    case file(URL)
    case bytes(Data)
}

enum ViewerState {
    case home(recents: [RecentEntry], error: String?)
    case loading
    case passwordNeeded(source: DocumentSource, displayName: String, sourceURL: URL?, wrongPassword: Bool)
    case viewing(displayName: String, pageSizes: [CGSize])
}

/// What the "Unsaved changes" alert resolves, once Save or Discard is picked (#145, #377,
/// #378): closing, sharing, or replacing the document with one just handed over from
/// outside. One alert, reused verbatim for all three — Dave's decision for #378 was to
/// reuse the existing dialog rather than invent a second one.
enum UnsavedChangesFollowUp: Equatable {
    case close
    case share
    case open(URL)
}

/// Owns the engine document and the ±2-page render window — the iOS port of
/// Android's `ViewerViewModel` (same virtualization, same eviction policy).
/// A signature stamp currently selected for move/resize/remove.
struct SelectedStamp: Equatable {
    let pageIndex: Int
    let annotIndex: Int
    let id: String
    let rect: PdfRect
}

/// A change waiting on "Change this page?" (#139): Continue or Cancel before a text box
/// regenerates a page PDFium's rewrite would alter.
struct PageRewriteWarning: Identifiable, Equatable {
    let id = UUID()
    let pageIndex: Int
}

/// A text box currently selected for drag/correct/remove (#36).
struct SelectedTextBox: Equatable {
    let pageIndex: Int
    let id: String
    let text: String
    let fontSize: Double
    let fontName: String
    let rect: PdfRect
}

/// An area marked for redaction, selected for drag/resize/remove (#329). The mark's own id
/// comes from the core and is stable for the mark's life, so this holds it rather than an
/// index into the overlay's list.
struct SelectedRedactionMark: Equatable {
    let pageIndex: Int
    let markId: Int
    let rect: PdfRect
}

/// Sizes offered for added text (#43). A short list, not a free-entry number box:
/// the job is "match the form I am filling in", and six presets cover it.
let textSizes: [Double] = [8, 10, 12, 14, 18, 24]

/// What a new box starts at, before the user has chosen anything this session.
let defaultTextSize: Double = 12

/// A tap that is waiting for the text the user is about to type (#34). When
/// `editingId` is set the tap re-opened an existing box to correct it (#36), and
/// (`x`, `y`) is that box's bounds lower-left rather than the raw tap point.
/// A line of the document's own text being retyped (#113).
struct PendingBodyEdit: Identifiable {
    let id = UUID()
    let pageIndex: Int
    let line: PdfTextLine
}

struct PendingText: Identifiable {
    let id = UUID()
    let pageIndex: Int
    let x: Double
    let y: Double
    var editingId: String?
    var fontSize: Double = defaultTextSize
    var fontName: String = PdfEngine.defaultFont
    var initialText: String = ""
}

/// One search hit in the document-wide flat match list (#26).
struct SearchMatch: Equatable {
    let pageIndex: Int
    let rects: [PdfRect]
}

@MainActor
final class ViewerModel: ObservableObject {
    @Published private(set) var state: ViewerState
    @Published private(set) var pageImages: [Int: CGImage] = [:]
    @Published private(set) var isDirty = false
    @Published private(set) var isSaving = false
    @Published var statusMessage: String?
    /// A second line under `statusMessage`, cleared with it: what a redaction removed, when it
    /// went out with the save that wrote it.
    @Published var statusDetail: String?
    /// Set when Share (#378) has a file ready to hand to the OS: ViewerView presents
    /// `UIActivityViewController` for it. Always `sourceURL` — Share hands over the document
    /// as currently on disk, so unlike Save a copy there is nothing to serialize freshly.
    @Published var shareURL: URL?
    /// An action waiting on the "Unsaved changes" alert (#377, #378): nil the rest of the
    /// time. Set here rather than as view-local state because `openExternal` needs to raise
    /// the same alert and runs before any view has a gesture to hang state off.
    @Published var unsavedChangesFollowUp: UnsavedChangesFollowUp?
    @Published private(set) var signatures: [SignatureEntry] = []
    @Published private(set) var pendingSignature: SignatureEntry?
    @Published private(set) var selectedStamp: SelectedStamp?
    /// The text box currently selected for drag/correct/remove (#36).
    @Published private(set) var selectedTextBox: SelectedTextBox?
    @Published private(set) var searchMatches: [SearchMatch] = []
    @Published private(set) var currentMatchIndex: Int?
    @Published private(set) var isSearching = false
    /// Undo/redo availability (#34) — mirrored out of the history for the toolbar.
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    /// True between "Add text" and the tap that says where it goes.
    @Published private(set) var isPlacingText = false
    /// Set by that tap; ViewerView presents the text field for it.
    @Published var pendingText: PendingText?
    /// What the text sheet is bound to. Seeded here rather than in the view, so a
    /// correction's prefill (#36/#43) lands in the same update as `pendingText`
    /// instead of racing the sheet's presentation.
    @Published var draftText = ""

    /// The line the body-text editor is open on, and what its field holds (#113).
    @Published var pendingBodyEdit: PendingBodyEdit?
    @Published var bodyDraft = ""
    /// A one-line notice over the page that clears itself — not an alert.
    @Published private(set) var notice: String?
    /// #139: a text-box change waiting for Continue or Cancel, on a page PDFium's rewrite would alter.
    @Published private(set) var pageRewriteWarning: PageRewriteWarning?
    private var noticeTask: Task<Void, Never>?
    /// The scanned-page hint is shown once per document, not on every stray tap.
    private var scannedHintShown = false
    @Published var draftSize = defaultTextSize
    @Published var draftFont = PdfEngine.defaultFont

    /// The open document's security and what it lets the tools do (#131). Unprotected
    /// until a document is open; reset on close.
    @Published private(set) var security: PdfSecurity = .unprotected
    var capabilities: DocumentCapabilities { DocumentCapabilities(security: security) }
    /// The Password command's sheet, and what it says when an unlock fails (#131).
    @Published var securitySheet: SecuritySheetMode?
    @Published private(set) var securityError: String?
    @Published private(set) var isUnlocking = false

    /// The size and face the last box was given (#43). Sticky for the session, so
    /// filling six fields on one form is not six trips through the pickers. Not
    /// persisted: a new document is usually a new job.
    private var lastFontSize = defaultTextSize
    private var lastFontName = PdfEngine.defaultFont

    /// Set only by `-screenshot sign|draw` launches; ViewerView opens the sheet.
    enum ScreenshotSheet { case signatures, draw }
    @Published private(set) var screenshotSheet: ScreenshotSheet?

    /// Set only by `-screenshot search` launches; ViewerView opens the find
    /// bar with this term already entered and runs the search immediately.
    @Published private(set) var screenshotSearchTerm: String?

    private let recents = RecentsStore()
    private let signatureStore = SignatureStore()
    private let history = EditHistory()

    // MARK: - Redaction (SDD §3.8 / F7, #173)
    //
    // A mark is the core's own and is never written to the file, so nothing here changes
    // the document: the view draws the marks, and applying is a separate, confirmed step
    // taken on save.

    /// The Redact tool is armed: the next drag across a page marks an area.
    @Published private(set) var redactMode = false

    /// Every mark on the document, by page, for the overlay that draws them.
    @Published private(set) var redactionMarks: [Int: [PdfRedactionMark]] = [:]

    /// The mark the user has selected for move, resize or removal (#329).
    @Published private(set) var selectedRedactionMark: SelectedRedactionMark?

    /// The summary after a redaction, shown once and dismissed.
    @Published var redactionSummary: String?
    /// The same summary held for the save or the copy that follows: it goes out with "Saved"
    /// as one alert. Raised on its own at the moment the save began, it collided with what
    /// came next — with "Saved" neither showed (#276), and the export sheet never appeared (#279).
    private var summaryForSave: String?

    /// Why a redaction refused, shown once and dismissed. Nothing was removed.
    @Published var redactionRefusal: String?

    /// How many areas are marked: what the save confirmation asks before it offers a copy.
    ///
    /// Derived from the overlay's map, which has exactly one writer and is cleared with the
    /// document (#329) — the map is what asked about redacting a document that had no marks
    /// when it outlived the core behind it.
    var redactionMarkCount: Int { redactionMarks.values.reduce(0) { $0 + $1.count } }

    func toggleRedactMode() {
        redactMode.toggle()
    }

    func cancelRedactMode() { redactMode = false }

    /// Whether this open may mark at all — what the Redact menu item asks before it offers
    /// itself (#328).
    var canRedact: Bool { capabilities.canEditContent }

    /// Marks the dragged area. A drag across text marks the text, grown to whole glyphs, so
    /// half a glyph is never left behind; a drag across a picture marks the rectangle.
    ///
    /// One drag is one undo step (#329), whatever the core made of it: a drag down six lines
    /// is six marks and one press of Undo. The ids come back from the engine rather than a
    /// count, which is what lets Undo name what the gesture made — and that is why this
    /// **records** the operation instead of performing it: the engine already made the marks
    /// as it answered "did that cross any text?".
    func markForRedaction(pageIndex: Int, rect: PdfRect) {
        guard let doc = document else { return }
        Task { @MainActor in
            let engine = PdfEngine.shared
            var made = (try? await engine.markTextForRedaction(doc, pageIndex: pageIndex, rect: rect)) ?? []
            if made.isEmpty {
                let id = try? await engine.markForRedaction(doc, pageIndex: pageIndex, rect: rect)
                if let id, id >= 0 { made = [id] }
            }
            await refreshRedactionMarks()
            redactMode = false
            guard !made.isEmpty else { return }
            // The rectangles to remember are the ones the core ended up with, not the ones
            // the drag asked for: the glyph snapping moved them, and redo has to put back
            // what the person saw.
            let rects = (redactionMarks[pageIndex] ?? [])
                .filter { made.contains($0.markId) }
                .map(\.rect)
            history.record(RedactMarkOperation(pageIndex: pageIndex, rects: rects,
                                               ids: made, adding: true))
            canUndo = history.canUndo
            canRedo = history.canRedo
        }
    }

    /// Selects a mark, or clears the selection when it is the one already selected (#329).
    /// Removal is the ✕, the VoiceOver action or Delete — never a bare tap: a stray touch
    /// putting a mark on a document with no way back is what this replaces.
    func selectRedactionMark(pageIndex: Int, markId: Int) {
        if selectedRedactionMark?.pageIndex == pageIndex, selectedRedactionMark?.markId == markId {
            selectedRedactionMark = nil
            return
        }
        guard let mark = redactionMarks[pageIndex]?.first(where: { $0.markId == markId }) else { return }
        // One selection at a time, as with stamps and text boxes.
        selectedStamp = nil
        selectedTextBox = nil
        selectedRedactionMark = SelectedRedactionMark(pageIndex: pageIndex, markId: markId,
                                                     rect: mark.rect)
    }

    func deselectRedactionMark() {
        selectedRedactionMark = nil
    }

    /// Removes the selected mark, through the history so Undo puts it back.
    func removeSelectedRedactionMark() {
        guard let selected = selectedRedactionMark else { return }
        removeRedactionMark(pageIndex: selected.pageIndex, markId: selected.markId)
    }

    func removeRedactionMark(pageIndex: Int, markId: Int) {
        guard let doc = document, canRedact else { return }
        Task { @MainActor in
            guard let rect = redactionMarks[pageIndex]?.first(where: { $0.markId == markId })?.rect
            else { return }
            selectedRedactionMark = nil
            try? await perform(
                RedactMarkOperation(pageIndex: pageIndex, rects: [rect], ids: [markId],
                                    adding: false),
                doc: doc)
            announce(String(localized: "Mark removed."))
        }
    }

    /// Removes every mark on the document, as one undo step (#329).
    func clearRedactionMarks() {
        guard let doc = document, canRedact, redactionMarkCount > 0 else { return }
        let marksByPage = redactionMarks.mapValues { $0.map(\.rect) }
        selectedRedactionMark = nil
        Task { @MainActor in
            try? await perform(
                ClearRedactionMarksOperation(pageIndex: marksByPage.keys.min() ?? 0,
                                             marksByPage: marksByPage),
                doc: doc)
            announce(String(localized: "Marks cleared."))
        }
    }

    /// Moving or resizing the selected mark, one undo step per gesture, clamped to the page
    /// (#329). A resize keeps the rectangle the person drew: it does not re-snap to glyphs,
    /// because apply removes by intersection with the drawn area and re-snapping under a
    /// finger would make the box jump.
    func commitRedactionMarkRect(pageIndex: Int, markId: Int, rect: PdfRect) {
        guard let doc = document, canRedact,
              case let .viewing(_, pageSizes) = state, pageIndex < pageSizes.count,
              let from = selectedRedactionMark,
              from.pageIndex == pageIndex, from.markId == markId else { return }
        let clamped = clampToPage(rect, pageSize: pageSizes[pageIndex])
        guard clamped != from.rect else { return }
        selectedRedactionMark = SelectedRedactionMark(pageIndex: pageIndex, markId: markId,
                                                     rect: clamped)
        Task { @MainActor in
            try? await perform(
                MoveRedactionMarkOperation(pageIndex: pageIndex, markId: markId,
                                           from: from.rect, to: clamped),
                doc: doc)
        }
    }

    /// Says something to VoiceOver with nothing on screen: a removed mark is already visible
    /// on the page, so a dialog over it would be noise.
    private func announce(_ text: String) {
        UIAccessibility.post(notification: .announcement, argument: text)
    }

    /// Reads every page's marks off the core and states the result.
    ///
    /// The one writer of `redactionMarks`, and it says the truth even when the truth is
    /// "there is nothing": with no document open there are no marks (#329). Returning early
    /// instead is what let a previous document's marks be drawn over the next one, and —
    /// because the count is derived from this map — made Save ask about redacting a document
    /// that had no marks at all.
    private func refreshRedactionMarks() async {
        guard case let .viewing(_, pageSizes) = state, let doc = document else {
            redactionMarks = [:]
            selectedRedactionMark = nil
            return
        }
        var byPage: [Int: [PdfRedactionMark]] = [:]
        for index in 0..<pageSizes.count {
            if let found = try? await PdfEngine.shared.redactionMarks(doc, pageIndex: index), !found.isEmpty {
                byPage[index] = found
            }
        }
        redactionMarks = byPage
        // The selection is a view of a mark that may have moved, been removed or gone with
        // the document; it is re-read from what the core actually carries.
        if let selected = selectedRedactionMark {
            guard let mark = byPage[selected.pageIndex]?.first(where: { $0.markId == selected.markId })
            else {
                selectedRedactionMark = nil
                return
            }
            selectedRedactionMark = SelectedRedactionMark(pageIndex: selected.pageIndex,
                                                         markId: selected.markId,
                                                         rect: mark.rect)
        }
    }

    /// Applies every mark. True when the document was redacted and may be saved; false when
    /// it refused — and then NOTHING was removed and the marks are still there.
    ///
    /// `reportWithSave` holds what was removed for the save or the copy the caller starts next,
    /// which reports it under "Saved" (or on its own, if the copy is cancelled), rather than
    /// raising an alert now over the save that is about to begin.
    @discardableResult
    func applyRedactions(reportWithSave: Bool = false) async -> Bool {
        guard let doc = document, redactionMarkCount > 0 else { return true }
        let report = await PdfEngine.shared.applyRedactions(doc)
        guard report.applied else {
            redactionRefusal = Self.describeRefusal(report)
            await refreshRedactionMarks()
            return false
        }
        // Undo cannot put the removed content back: the core freed the objects the history
        // was holding for exactly that.
        history.clear()
        canUndo = false
        canRedo = false
        redactionMarks = [:]
        selectedRedactionMark = nil
        redactMode = false
        if reportWithSave {
            summaryForSave = Self.describeRedaction(report.counts)
        } else {
            redactionSummary = Self.describeRedaction(report.counts)
        }
        // Every page's raster is stale once content has been removed.
        pageImages.removeAll()
        renderedWidths.removeAll()
        if let window = lastWindow {
            updateRenderWindow(first: window.0, last: window.1, widthPx: window.2)
        }
        return true
    }

    static func describeRedaction(_ counts: PdfRedactionCounts) -> String {
        let removed = PdfRedactionSummary.removed(
            counts,
            plural: { kind, n in
                switch kind {
                case .characters: return String(format: String(localized: "%@ characters"), "\(n)")
                case .images: return String(format: String(localized: "%@ images"), "\(n)")
                case .formFields: return String(format: String(localized: "%@ form fields"), "\(n)")
                case .annotations: return String(format: String(localized: "%@ annotations"), "\(n)")
                }
            },
            singular: { kind in
                switch kind {
                case .characters: return String(localized: "1 character")
                case .images: return String(localized: "1 image")
                case .formFields: return String(localized: "1 form field")
                case .annotations: return String(localized: "1 annotation")
                }
            },
            nothing: String(localized: "nothing"))
        return counts.areas == 1
            ? String(format: String(localized: "1 area redacted: %@"), removed)
            : String(format: String(localized: "%@ areas redacted: %@"), "\(counts.areas)", removed)
    }

    static func describeRefusal(_ report: PdfRedactionReport) -> String {
        if report.needsPermission {
            return String(localized: "This document doesn't allow changes, so it can't be redacted.")
        }
        let refusal = report.refusals.first
        let why: String
        switch refusal?.reason {
        case .type3Font, .fontCannotRedraw:
            why = String(localized: "The text there is in a font MegaPDF can't redraw around your marks.")
        case .formXObject:
            why = String(localized: "Part of that area is drawn from a shared block MegaPDF can't take apart safely.")
        case .layoutGuard:
            why = String(localized: "Removing it would change the page outside the areas you marked.")
        default:
            why = String(localized: "MegaPDF couldn't take that content apart safely.")
        }
        let page = (refusal?.pageIndex ?? 0) + 1
        let body = String(format: String(localized:
            "MegaPDF couldn't remove everything you marked on page %@, so it removed nothing and left the file as it was."),
            "\(page)")
        return body + " " + why
    }
    private(set) var document: PdfDocument?

    /// Feedback while the app works (#145): what is running, and the indicators for it. Views
    /// observe it directly. Page-level work ends with the document; document-level work (an open,
    /// a save) belongs to whoever started it.
    let busy = BusyState()

    /// The #139 page check, one shared check per page of the open document, started early (#145).
    /// Settled pages need no warning before a text-box change: they keep their look, the person
    /// already chose Continue, or a change went on without one.
    private(set) lazy var pageChecks = PageCheckCoordinator { [weak self] page in
        guard let self, let doc = self.document else { return .cancelled }
        return await self.pageCheck(doc, page)
    }

    /// How a page is checked. Tests substitute their own answers.
    var pageCheck: @MainActor (PdfDocument, Int) async -> PageCheckAnswer = { doc, page in
        await PdfEngine.shared.pageCheck(doc, pageIndex: page)
    }

    /// The page at the top of the view, which gets a background page check once it has settled there.
    private var currentPage: Int?
    private var pageShownTask: Task<Void, Never>?
    private static let pageShownDelayNanos: UInt64 = 400_000_000

    /// Bumped by every change to the document (D3, #145): a save marks the document saved only
    /// if nothing changed while it ran.
    private(set) var editCount = 0
    /// `editCount` when the bytes for Save a copy were made.
    private var exportEditCount: Int?

    private var searchToken: BusyToken?
    private var pageRewriteContinuation: CheckedContinuation<Bool, Never>?
    private var sourceURL: URL?
    /// The picked file whose security-scoped access is held while its document is open (#147):
    /// the document reads the file for as long as it is open, and Save writes it.
    private var scopedAccessURL: URL?
    /// The staged copy the Save-a-copy exporter writes from, removed once it has (#147).
    private var exportStagedURL: URL?
    private var renderedWidths: [Int: Int] = [:]
    private var renderTask: Task<Void, Never>?
    private var searchTask: Task<Void, Never>?
    private var lastWindow: (first: Int, last: Int, widthPx: Int)?

    /// Margin added to a text box's tight glyph rect when hit-testing a tap (#36).
    private static let tapSlopPoints: Double = 6

    private static let renderMargin = 2
    private static let maxPixelDim = 2048

    init() {
        state = .home(recents: recents.load(), error: nil)
        refreshRecents()
        signatures = signatureStore.load()
        applyScreenshotModeIfNeeded()
        applyUITestDocumentIfNeeded()
    }

    /// Debug builds only: a UI test hands over a small PDF as base64 in the launch
    /// environment, so the editing tiers can be driven on documents built for them.
    private func applyUITestDocumentIfNeeded() {
        #if DEBUG
        guard let base64 = ProcessInfo.processInfo.environment["MEGAPDF_UITEST_PDF_BASE64"],
              let bytes = Data(base64Encoded: base64),
              let token = busy.begin(.opening, scope: .document, blocksFileCommands: true) else { return }
        Task {
            defer { busy.end(token) }
            await open(source: .bytes(bytes), password: nil, displayName: "UITest.pdf", sourceURL: nil)
        }
        #endif
    }

    private func applyScreenshotModeIfNeeded() {
        guard let mode = DemoContent.requestedState else { return }
        if signatures.isEmpty, let image = DemoContent.signatureImage(),
           let entry = signatureStore.add(displayName: "Mega W.", image: image) {
            signatures.append(entry)
        }
        switch mode {
        case "home":
            state = .home(recents: DemoContent.demoRecents(), error: nil)
        // The #165 evidence shot: one file name from four places, one of them gone.
        case "recents":
            let scenario = DemoContent.recentsScenario()
            unavailableRecentIDs = scenario.unavailable
            state = .home(recents: scenario.entries, error: nil)
        case "viewer", "sign", "draw", "search", "text", "text-edit", "story", "redact":
            let resource = mode == "story" ? DemoContent.blankDemoResource : DemoContent.demoResource
            if let url = Bundle.main.url(forResource: resource, withExtension: "pdf"),
               let bytes = try? Data(contentsOf: url) {
                if mode == "sign" { screenshotSheet = .signatures }
                if mode == "draw" { screenshotSheet = .draw }
                if mode == "search" { screenshotSearchTerm = DemoContent.searchTerm }
                Task {
                    await open(source: .bytes(bytes), password: nil,
                               displayName: DemoContent.documentName, sourceURL: nil)
                    if mode == "redact", let doc = document {
                        // The state the feature has to be legible in (#173): a line of the
                        // demo agreement marked — a translucent box you can still read
                        // through, over text you are about to remove — with its selection
                        // chrome, so the shot also shows that a mark can be taken off
                        // (#329). The line is found by what it says rather than by a
                        // rectangle that has to be right, so the shot lands on a sentence
                        // in every language.
                        //
                        // Not armed, though the pose used to arm it: since #328 the tool is
                        // a row in the ⋯ menu, so an armed tool draws nothing on the page to
                        // photograph. A selected mark does.
                        let lines = (try? await PdfEngine.shared.textLines(doc, pageIndex: 0)) ?? []
                        let line = lines.first { $0.text.contains(DemoContent.redactedWord) }
                            ?? lines.max { $0.text.count < $1.text.count }
                        if let line {
                            markForRedaction(pageIndex: 0, rect: line.rect)
                            // The mark lands asynchronously, so this waits for it rather
                            // than guessing at a delay: this pose is a store capture, and a
                            // missed selection would be a different picture from the one
                            // that was checked.
                            var waited = 0
                            while redactionMarks[0]?.isEmpty != false, waited < 5_000 {
                                try? await Task.sleep(nanoseconds: 50_000_000)
                                waited += 50
                            }
                            if let mark = redactionMarks[0]?.first {
                                selectRedactionMark(pageIndex: 0, markId: mark.markId)
                            }
                        }
                    }
                    if mode == "text-edit", let doc = document,
                       let line = try? await PdfEngine.shared.textLines(doc, pageIndex: 0).first {
                        // The body-text editor open on the agreement's heading, mid-correction (#113).
                        bodyDraft = String(localized: "Equipment Rental Agreement (2026)",
                                           comment: "screenshot: the demo heading being corrected in the text editor")
                        pendingBodyEdit = PendingBodyEdit(pageIndex: 0, line: line)
                    }
                    if mode == "text" {
                        // The Add text sheet, open on a typed name with the size
                        // and face pickers showing (#43). Armed after the open so
                        // it cannot be cleared by the state change, and with a
                        // chosen tap point rather than a synthesised one: just
                        // under the signature rule, where a printed name belongs.
                        draftText = DemoContent.printedName
                        pendingText = PendingText(pageIndex: 0,
                                                  x: DemoContent.printedNameX,
                                                  y: DemoContent.printedNameY,
                                                  initialText: DemoContent.printedName)
                    }
                }
            }
        default:
            break
        }
    }

    // MARK: - opening

    func openPicked(url: URL) {
        // The home screen is the only way in, so no document is open here; a second tap waits.
        guard let token = busy.begin(.opening, scope: .document, blocksFileCommands: true) else { return }
        state = .loading
        Task {
            defer { busy.end(token) }
            // Opened from the file, read on demand (#147, #148): nothing reads it whole, so a
            // file of any size opens in the time PDFium takes to parse its structure.
            await open(source: .file(url), password: nil,
                       displayName: url.lastPathComponent, sourceURL: url)
        }
    }

    /// A document handed to the app from outside (#377): Mail, Files, another app's share
    /// sheet, or a Files "Open In Place" launch — anything reaching here through
    /// `.onOpenURL` rather than the in-app picker. The only difference from `openPicked`
    /// is that nothing has already chosen to close whatever is open, so a dirty document
    /// asks first, through the same alert Close and Share already use — silently replacing
    /// it would be #377 trading one data-loss bug for another.
    func openExternal(url: URL) {
        // A save, an open already in flight, or a change being applied still needs the
        // current document; the OS redelivers "Open In…" on the next launch/foreground if
        // this one is missed, so dropping it here (rather than queuing it) is safe.
        guard !busy.isBlocked else { return }
        if isDirty {
            unsavedChangesFollowUp = .open(url)
        } else {
            openPicked(url: url)
        }
    }

    func openRecent(_ entry: RecentEntry) {
        guard !busy.isBlocked else { return }
        state = .loading
        Task {
            guard let bookmark = entry.bookmarkData else {
                recents.remove(id: entry.id)
                toHome(String(localized: "That entry was unreadable and has been removed."))
                return
            }
            var stale = false
            guard let url = try? URL(
                resolvingBookmarkData: bookmark, options: [],
                relativeTo: nil, bookmarkDataIsStale: &stale)
            else {
                // Kept, not dropped (#165): the row greys out the way the Files app greys
                // out an item that is not there, and its menu offers Remove from Recents.
                // A file on a cloud drive or an external disk comes back, and a list that
                // deleted the entry would have thrown away the way back to it.
                unavailableRecentIDs.insert(entry.id)
                toHome(String(localized: "That file is no longer accessible. Pick it again to reopen it."))
                return
            }
            openPicked(url: url)
        }
    }

    // MARK: - recents (#165)

    /// Entries whose file is no longer where it was, so their rows grey out.
    ///
    /// Held here rather than stored: a file comes back when a drive is plugged in or
    /// an iCloud download finishes, and a list that remembered "gone" would be wrong
    /// the moment it did.
    @Published private(set) var unavailableRecentIDs: Set<String> = []

    private var recentsRefresh: Task<Void, Never>?

    /// What one pass over the stored entries found.
    private struct RecentsScan: Sendable {
        var unavailable: Set<String> = []
        /// Locations for the entries stored before #165, which have none.
        var filled: [String: RecentLocation] = [:]
    }

    /// Fills in the locations of entries written before #165, and marks the ones
    /// whose file has gone.
    ///
    /// Deliberately after the list is already on screen, and off the main actor: this
    /// resolves bookmarks, and resolving ten of them — some of them cloud files — is
    /// not work to do while a view is being laid out. That is the whole reason the
    /// location is recorded at open time instead. Each entry is written back once, so
    /// the next launch has nothing to do.
    func refreshRecents() {
        // A screenshot launch shows made-up recents, which are in no store and must
        // not be replaced by what is.
        guard DemoContent.requestedState == nil else { return }
        recentsRefresh?.cancel()
        let entries = recents.load()
        guard !entries.isEmpty else {
            unavailableRecentIDs = []
            return
        }
        let device = RecentLocation.deviceName()
        recentsRefresh = Task { [weak self] in
            let scan = await Task.detached(priority: .utility) {
                ViewerModel.scanRecents(entries, deviceName: device)
            }.value
            guard !Task.isCancelled, let self else { return }
            self.unavailableRecentIDs = scan.unavailable
            guard !scan.filled.isEmpty else { return }
            for (id, location) in scan.filled {
                self.recents.setLocation(location, id: id)
            }
            if case let .home(_, error) = self.state {
                self.state = .home(recents: self.recents.load(), error: error)
            }
        }
    }

    /// The file-system half of `refreshRecents`, off the main actor.
    private nonisolated static func scanRecents(_ entries: [RecentEntry],
                                                deviceName: String) -> RecentsScan {
        var scan = RecentsScan()
        for entry in entries {
            if Task.isCancelled { return scan }
            var stale = false
            guard let bookmark = entry.bookmarkData,
                  let url = try? URL(resolvingBookmarkData: bookmark, options: [],
                                     relativeTo: nil, bookmarkDataIsStale: &stale)
            else {
                scan.unavailable.insert(entry.id)
                continue
            }
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            if !FileManager.default.fileExists(atPath: url.path) {
                scan.unavailable.insert(entry.id)
            }
            if entry.location == nil, let location = RecentLocation.of(url, deviceName: deviceName) {
                scan.filled[entry.id] = location
            }
        }
        return scan
    }

    /// Takes a row off the list, from its context menu.
    func removeRecent(id: String) {
        let updated = recents.remove(id: id)
        unavailableRecentIDs.remove(id)
        if case let .home(_, error) = state {
            state = .home(recents: updated, error: error)
        }
    }

    /// Reveals a recent document in the Files app, from its context menu (#165).
    ///
    /// `shareddocuments:` is how the Files app is asked to reveal a path. It is a URL
    /// scheme rather than any private interface, and it is not declared in
    /// `LSApplicationQueriesSchemes`, so `canOpenURL` cannot be asked in advance and
    /// the answer comes back from `open` instead — the spec for this asks for the
    /// item "where the system supports it", and a place the system will not reveal
    /// says so rather than doing nothing.
    func showRecentInFiles(_ entry: RecentEntry) {
        guard let bookmark = entry.bookmarkData else { return }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: bookmark, options: [],
                                 relativeTo: nil, bookmarkDataIsStale: &stale),
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else {
            unavailableRecentIDs.insert(entry.id)
            statusMessage = String(localized: "That file is no longer accessible. Pick it again to reopen it.")
            return
        }
        components.scheme = "shareddocuments"
        guard let filesURL = components.url else { return }
        UIApplication.shared.open(filesURL, options: [:]) { [weak self] opened in
            guard !opened else { return }
            self?.statusMessage = String(
                localized: "Files couldn't show that location.",
                comment: "#165: the Files app refused to reveal a recent document")
        }
    }

    func submitPassword(_ password: String) {
        guard case let .passwordNeeded(source, displayName, url, _) = state,
              let token = busy.begin(.opening, scope: .document, blocksFileCommands: true) else { return }
        state = .loading
        Task {
            defer { busy.end(token) }
            await open(source: source, password: password,
                       displayName: displayName, sourceURL: url)
        }
    }

    private func open(source: DocumentSource, password: String?,
                      displayName: String, sourceURL: URL?) async {
        do {
            let doc: PdfDocument
            switch source {
            case let .bytes(bytes):
                doc = try await PdfEngine.shared.open(bytes, password: password)
            case let .file(url):
                // Off the main actor: PDFium reads the file's structure here, and a file provider
                // may still be fetching it.
                let scoped = url.startAccessingSecurityScopedResource()
                defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                doc = try await PdfEngine.shared.open(file: url, password: password)
            }
            let count = await PdfEngine.shared.pageCount(doc)
            var sizes: [CGSize] = []
            for i in 0..<count {
                sizes.append(try await PdfEngine.shared.pageSize(doc, index: i))
            }
            let openedSecurity = await PdfEngine.shared.security(doc)
            closeCurrent()
            document = doc
            security = openedSecurity
            self.sourceURL = sourceURL
            // Held for the document's life (#147): it reads its file on demand, and Save writes it.
            if let sourceURL, sourceURL.startAccessingSecurityScopedResource() {
                scopedAccessURL = sourceURL
            }
            if let sourceURL,
               let bookmark = try? sourceURL.bookmarkData() {
                // Where the file lives, recorded now while the app holds the URL and the
                // right to read around it (#165). Off the main actor: it stats the parent
                // folder, and for a cloud file that is a round trip.
                let device = RecentLocation.deviceName()
                let location = await Task.detached(priority: .utility) {
                    RecentLocation.of(sourceURL, deviceName: device)
                }.value
                recents.add(RecentEntry(
                    bookmarkBase64: bookmark.base64EncodedString(),
                    displayName: displayName,
                    lastOpenedEpochMs: Int64(Date().timeIntervalSince1970 * 1000),
                    location: location))
                unavailableRecentIDs.remove(bookmark.base64EncodedString())
            }
            state = .viewing(displayName: displayName, pageSizes: sizes)
            // A fresh core carries no marks, and the refresh is what says so (#329): the map
            // is what Save reads to decide whether to ask about redaction, so it states the
            // truth from the moment a document is on screen rather than only after the first
            // mark is made.
            await refreshRedactionMarks()
            // A restricted open says so, and where the owner password goes (ADR-004 §3).
            if capabilities.isRestricted { showRestrictedNotice() }
        } catch PdfError.passwordRequired {
            state = .passwordNeeded(source: source, displayName: displayName,
                                    sourceURL: sourceURL, wrongPassword: password != nil)
        } catch PdfError.unsupportedSecurity {
            // Not corrupt and not a wrong password: say which it is (ADR-004 §8).
            toHome(String(localized: "This PDF uses a kind of protection MegaPDF can't open."))
        } catch PdfError.tooLarge {
            toHome(String(localized: "This file is too large for MegaPDF to open."))
        } catch PdfError.load(code: 2) {   // FPDF_ERR_FILE: gone, or not readable
            toHome(String(localized: "Couldn't read that file."))
        } catch {
            // A plain sentence, not the raw error: the engine's own description is
            // not something the home screen should print.
            toHome(String(localized: "Couldn't open that file."))
        }
    }

    // MARK: - rendering

    /// Visible range changed: render visible ± margin at `widthPx` (capped),
    /// keep already-sharp pages, evict the rest.
    func updateRenderWindow(first: Int, last: Int, widthPx: Int) {
        guard case let .viewing(_, pageSizes) = state, let doc = document else { return }
        lastWindow = (first, last, widthPx)
        pageShown(first)
        let window = max(0, first - Self.renderMargin)...min(pageSizes.count - 1, last + Self.renderMargin)

        for index in pageImages.keys where !window.contains(index) {
            pageImages.removeValue(forKey: index)
            renderedWidths.removeValue(forKey: index)
        }

        renderTask?.cancel()
        renderTask = Task {
            for index in window {
                if Task.isCancelled { return }
                let size = pageSizes[index]
                // The app's memory bound, aspect preserved (a per-axis clamp squashed
                // tall pages); the engine applies its own render clamp on top (#93/#111).
                let idealWidth = Double(max(widthPx, 1))
                let idealHeight = idealWidth * size.height / size.width
                let memoryScale = min(1.0, Double(Self.maxPixelDim) / max(idealWidth, idealHeight))
                let width = max(1, Int(idealWidth * memoryScale))
                let height = max(1, Int(idealHeight * memoryScale))
                if renderedWidths[index] == width { continue }
                if let image = try? await PdfEngine.shared.render(
                    doc, index: index, pixelWidth: width, pixelHeight: height) {
                    pageImages[index] = image
                    renderedWidths[index] = width
                }
            }
        }
    }

    // MARK: - page checks (#139, #145)

    /// A page came to the top of the view. Its check starts once it has stayed there briefly, so
    /// scrolling past pages doesn't start and cancel a check for each one.
    private func pageShown(_ page: Int) {
        guard currentPage != page else { return }
        currentPage = page
        pageShownTask?.cancel()
        pageShownTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: Self.pageShownDelayNanos)
            guard !Task.isCancelled, let self, self.currentPage == page else { return }
            self.startPageCheck(page)
        }
    }

    /// Starts the page's check in the background, when this open may make changes that regenerate
    /// the page (today: text boxes). Cancels an unfinished check for any other page.
    private func startPageCheck(_ page: Int) {
        guard document != nil, capabilities.canAddText else { return }
        pageChecks.start(page)
    }

    /// Page-level blocking work for a change to `pageIndex`: taps, tools and other edits wait
    /// until it ends. Nil when other work is already running.
    private func beginPageChange(_ pageIndex: Int, near rect: PdfRect?) -> BusyToken? {
        busy.begin(.applying, scope: .page(pageIndex, rect), showsIndicator: false)
    }

    /// Close and Discard wait while a save or password change runs, or a change is being applied.
    var closeBlocked: Bool { busy.blocksFileCommands || busy.works.contains { $0.label == .applying && $0.showsIndicator } }

    /// Save, Save a copy, Password and Unlock wait while any blocking work runs.
    var fileCommandsBlocked: Bool { busy.blocksFileCommands }

    // MARK: - editing

    /// Tap dispatch — same ordering as desktop and Android: form fields win
    /// over page content, then existing marks (tap to remove), then drawn
    /// squares (tap to place). Fractions are tap position / page view size,
    /// top-left origin.
    func onPageTapped(index: Int, xFraction: Double, yFraction: Double) {
        guard case let .viewing(_, pageSizes) = state, let doc = document else { return }
        // Taps are ignored while other work runs (#145): never two changes or two questions at once.
        guard !busy.isBlocked else { return }
        let size = pageSizes[index]
        let x = xFraction * size.width
        let y = (1 - yFraction) * size.height  // view top-left → PDF bottom-left

        // A mark the user drew, which is drawn over the page, wins the tap (#329):
        // selecting it is the way in to moving, resizing and removing it. No page work is
        // involved — a mark is not page content — so this answers before the token, the
        // page check and the spinner. Tapping it again deselects.
        if let mark = redactionMarks[index]?.last(where: { $0.rect.contains(x: x, y: y) }) {
            selectRedactionMark(pageIndex: index, markId: mark.markId)
            return
        }
        // Anywhere else clears the selection, so the ✕ does not linger over a page the
        // user has moved on from.
        deselectRedactionMark()

        // Every branch that would change the document checks what this open may do
        // first (#131) and shows the restricted notice instead of editing.
        let caps = capabilities

        if let entry = pendingSignature {
            pendingSignature = nil
            guard permits(caps.canSign) else { return }
            placeSignature(entry, doc: doc, pageIndex: index, pageSize: size, x: x, y: y)
            return
        }

        if isPlacingText {
            isPlacingText = false
            statusMessage = nil
            guard permits(caps.canAddText) else { return }
            // The page the text goes on is known now: check it while the text is typed.
            startPageCheck(index)
            draftText = ""
            draftSize = lastFontSize
            draftFont = lastFontName
            pendingText = PendingText(pageIndex: index, x: x, y: y,
                                      fontSize: lastFontSize, fontName: lastFontName)
            return
        }

        guard let token = beginPageChange(index, near: nil) else { return }
        Task {
            defer { busy.end(token) }
            var applying = false
            do {
                let engine = PdfEngine.shared

                let allStamps = try await engine.stamps(doc, pageIndex: index)
                if let sig = allStamps.first(where: {
                    $0.id.hasPrefix("sig:") && $0.rect.contains(x: x, y: y)
                }) {
                    // Selecting is the way in to moving and removing it.
                    guard permits(caps.canSign) else { return }
                    selectedStamp = SelectedStamp(pageIndex: index, annotIndex: sig.annotIndex,
                                                  id: sig.id, rect: sig.rect)
                    selectedTextBox = nil
                    return
                }
                selectedStamp = nil

                // Text boxes (#36) rank with signatures: both are things the user
                // put on the page, so they win over the document underneath. Last
                // match wins — later page objects paint on top. The rect is tight
                // around the glyphs, and a 12 pt line is a few points tall, so the
                // hit test gets `tapSlopPoints` of margin.
                let boxes = try await engine.textBoxes(doc, pageIndex: index)
                if let box = boxes.last(where: {
                    $0.rect.grown(by: Self.tapSlopPoints).contains(x: x, y: y)
                }) {
                    if box.id.hasPrefix(PdfEngine.untaggedPrefix) {
                        // A box written by MegaPDF for Windows 1.6.x, before boxes
                        // carried an id. Its only handle is its page-object index,
                        // which the history would replay against a page whose
                        // indices had since shifted — so it would eventually move
                        // or delete the wrong box. Swallow the tap rather than let
                        // it fall through and toggle whatever is underneath.
                        selectedTextBox = nil
                        statusMessage = String(localized: "This text was added by an older version and can't be edited here.")
                        return
                    }
                    guard permits(caps.canAddText) else {
                        selectedTextBox = nil
                        return
                    }
                    guard document === doc else { return }
                    let wasSelected = selectedTextBox?.id == box.id
                    let selection = SelectedTextBox(pageIndex: index, id: box.id,
                                                    text: box.text,
                                                    fontSize: box.fontSize,
                                                    fontName: box.fontName,
                                                    rect: box.rect)
                    selectedTextBox = selection
                    // A selected box is a change about to happen: have the page's answer ready (#145).
                    startPageCheck(index)
                    if wasSelected {
                        // A second tap on the selected box also opens the editor.
                        // The overlay's pencil is the discoverable way in, because
                        // a *quick* second tap is claimed by double-tap-to-zoom —
                        // this path only fires after that disambiguation lapses.
                        openTextEditor(on: selection)
                    }
                    return
                }
                selectedTextBox = nil

                // Whichever edit the tap lands on, it goes through the history so
                // it can be taken back (#34).
                var operation: PdfEditOperation?
                let fields = try await engine.formFields(doc, pageIndex: index)
                if let field = fields.first(where: { $0.rect.contains(x: x, y: y) }) {
                    operation = FieldToggleOperation(pageIndex: index,
                                                     x: field.rect.centerX,
                                                     y: field.rect.centerY)
                } else if let mark = allStamps.first(where: {
                    $0.id.hasPrefix("mark:") && $0.rect.contains(x: x, y: y)
                }) {
                    operation = MarkOperation(
                        pageIndex: index,
                        square: MarkOperation.square(fromMark: mark.rect),
                        id: mark.id, adding: false)
                } else if let square = try await engine
                    .detectCheckboxSquares(doc, pageIndex: index)
                    .first(where: { $0.contains(x: x, y: y) }) {
                    operation = MarkOperation(pageIndex: index, square: square,
                                              id: "mark:\(UUID().uuidString)", adding: true)
                }
                if let operation {
                    // Form fields need fill-forms, marks need annotate (#131).
                    guard permits(caps.allows(operation)) else { return }
                    guard document === doc else { return }
                    applying = true
                    busy.update(token, label: .applying, showsIndicator: true)
                    try await perform(operation, doc: doc)
                    return
                }

                // Nothing the user placed and nothing to tick: the document's own text
                // (#113). A tap on a line opens the editor on it.
                let lines = try await engine.textLines(doc, pageIndex: index)
                if let line = lines.first(where: {
                    $0.rect.grown(by: Self.tapSlopPoints).contains(x: x, y: y)
                }) {
                    guard permits(caps.canEditContent) else { return }
                    // #118: on pages PDFium cannot rewrite faithfully, say so now rather
                    // than after the user has typed.
                    // #128: and say why — text elsewhere would move, or the page would look different.
                    // #145: a spinner on the line while the check runs; taps wait for it.
                    busy.update(token, label: .checkingPage, scope: .page(index, line.rect), showsIndicator: true)
                    var refusal: PdfLayoutCause?
                    for run in line.runs {
                        let verdict = try await engine.layoutVerdict(doc, pageIndex: index, objectIndex: run.objectIndex)
                        if verdict?.editable != true {
                            refusal = verdict?.cause ?? .rewriteFailed
                            break
                        }
                    }
                    // The document may have been closed while the check ran.
                    guard document === doc else { return }
                    if let refusal {
                        showNotice(refusal.notice)
                    } else {
                        bodyDraft = line.text
                        pendingBodyEdit = PendingBodyEdit(pageIndex: index, line: line)
                    }
                } else if lines.isEmpty, !scannedHintShown {
                    // Tier 3: a page with no text at all is a picture of a page.
                    scannedHintShown = true
                    showNotice(String(localized: "This page is a scanned image, so its text can't be edited."))
                }
            } catch {
                // Reading what was tapped can fail harmlessly and leaves the page unchanged;
                // a change that failed says so.
                if applying, document === doc {
                    statusMessage = String(localized: "Couldn't change the document.")
                }
            }
        }
    }

    // MARK: - the document's own text (#113)

    /// Commits the body-text editor. The same text is a no-op; an empty field removes
    /// the line. Either way it is one undoable edit.
    func commitBodyEdit(_ text: String) {
        guard let pending = pendingBodyEdit, let doc = document else { return }
        // Other work still running keeps the editor open rather than dropping what was typed.
        guard !busy.isBlocked else { return }
        pendingBodyEdit = nil
        guard permits(capabilities.canEditContent) else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed != pending.line.text else { return }
        guard let token = busy.begin(.applying, scope: .page(pending.pageIndex, pending.line.rect)) else { return }
        Task {
            defer { busy.end(token) }
            do {
                if trimmed.isEmpty {
                    try await perform(BodyTextDeleteOperation(pageIndex: pending.pageIndex, line: pending.line), doc: doc)
                } else {
                    let operation = BodyTextEditOperation(pageIndex: pending.pageIndex, line: pending.line, newText: trimmed)
                    try await perform(operation, doc: doc)
                    if operation.lastOutcome == .substituted {
                        showNotice(String(localized: "The original font couldn't show this text, so a similar standard font was used."))
                    }
                }
            } catch let PdfError.layoutWouldChange(cause) {
                showNotice(cause.notice)
            } catch {
                statusMessage = String(localized: "Couldn't change that text.")
            }
        }
    }

    func cancelBodyEdit() {
        pendingBodyEdit = nil
        bodyDraft = ""
    }

    /// Shows a notice over the page for a few seconds.
    func showNotice(_ text: String) {
        noticeTask?.cancel()
        notice = text
        noticeTask = Task {
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            if !Task.isCancelled { notice = nil }
        }
    }

    // MARK: - document security (#131)

    /// Why a restricted document won't take an edit, and how to get past it.
    private func showRestrictedNotice() {
        showNotice(String(localized: "The owner of this document has restricted changes. Unlock it with the owner password to edit it."))
    }

    /// Passes `allowed` through, showing the restricted notice when it is false — the
    /// check every editing entry point makes before it touches the document.
    private func permits(_ allowed: Bool) -> Bool {
        if !allowed { showRestrictedNotice() }
        return allowed
    }

    // MARK: - added text (#34)

    /// Arms the next tap to place text. Tapping the page opens the text field.
    func startTextPlacement() {
        guard !busy.isBlocked else { return }
        guard permits(capabilities.canAddText) else { return }
        cancelPlacement()
        selectedStamp = nil
        selectedTextBox = nil
        isPlacingText = true
        // Add text is armed: check the page in view now, so the answer is ready when the text is (#145).
        if let currentPage { startPageCheck(currentPage) }
        statusMessage = String(localized: "Tap the page where the text should go")
    }

    func cancelTextPlacement() {
        isPlacingText = false
        pendingText = nil
        draftText = ""
        statusMessage = nil
    }

    /// Commits what the text sheet was left holding — a new box, or a change to
    /// one already on the page. Text, size and face all arrive together, so
    /// restyling and correcting a typo are the same single undoable edit.
    func commitText(_ text: String, fontSize: Double, fontName: String) {
        guard let pending = pendingText, let doc = document else { return }
        // Other work still running keeps the sheet open rather than dropping what was typed.
        guard !busy.isBlocked else { return }
        pendingText = nil
        draftText = ""
        guard permits(capabilities.canAddText) else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        lastFontSize = fontSize
        lastFontName = fontName
        let style = TextBoxStyle(text: trimmed, fontSize: fontSize, fontName: fontName)
        let spot = PdfRect(left: pending.x, bottom: pending.y, right: pending.x, top: pending.y)
        guard let token = beginPageChange(pending.pageIndex, near: spot) else { return }
        Task {
            defer { busy.end(token) }
            do {
                if let editingId = pending.editingId {
                    let before = TextBoxStyle(text: pending.initialText,
                                              fontSize: pending.fontSize,
                                              fontName: pending.fontName)
                    guard before != style else { return }
                    guard await confirmPageRewrite(doc, pageIndex: pending.pageIndex, token: token) else {
                        await reselectTextBox(doc, pageIndex: pending.pageIndex, id: editingId)
                        return
                    }
                    try await perform(
                        EditTextBoxOperation(pageIndex: pending.pageIndex, id: editingId,
                                             from: before, to: style,
                                             x: pending.x, y: pending.y),
                        doc: doc)
                    await reselectTextBox(doc, pageIndex: pending.pageIndex, id: editingId)
                } else {
                    guard await confirmPageRewrite(doc, pageIndex: pending.pageIndex, token: token) else { return }
                    try await perform(
                        TextBoxOperation(pageIndex: pending.pageIndex,
                                         id: "text:\(UUID().uuidString)",
                                         text: trimmed, fontSize: fontSize,
                                         x: pending.x, y: pending.y, adding: true,
                                         fontName: fontName),
                        doc: doc)
                }
            } catch {
                if pending.editingId != nil {
                    statusMessage = String(localized: "Couldn't change that text.")
                } else {
                    statusMessage = String(localized: "Couldn't add that text.")
                }
            }
        }
    }

    // MARK: - selected text box: drag, correct, remove (#36)

    /// Commits a drag from the selection overlay. Only the position changes — a
    /// text box has no resize handle, because resizing one would mean changing
    /// its font size, and SDD §3.1 keeps formatting controls out of the app.
    func commitTextBoxRect(_ newRect: PdfRect) {
        guard let sel = selectedTextBox, let doc = document,
              case let .viewing(_, pageSizes) = state else { return }
        // A drag while other work runs springs back: the overlay has already let go of it.
        guard !busy.isBlocked else { return }
        guard permits(capabilities.canAddText) else { return }
        let rect = clampToPage(newRect, pageSize: pageSizes[sel.pageIndex])
        // A tap that slipped into a drag can land a sub-point move; don't put a
        // no-op on the undo stack for it.
        guard abs(rect.left - sel.rect.left) >= 0.01
                || abs(rect.bottom - sel.rect.bottom) >= 0.01 else { return }
        guard let token = beginPageChange(sel.pageIndex, near: sel.rect) else { return }
        Task {
            defer { busy.end(token) }
            do {
                guard await confirmPageRewrite(doc, pageIndex: sel.pageIndex, token: token) else {
                    // Cancelled: the box stays where it was; republishing the selection
                    // puts the overlay back on it.
                    if selectedTextBox?.id == sel.id { selectedTextBox = sel }
                    return
                }
                try await perform(
                    MoveTextBoxOperation(pageIndex: sel.pageIndex, id: sel.id,
                                         from: (x: sel.rect.left, y: sel.rect.bottom),
                                         to: (x: rect.left, y: rect.bottom)),
                    doc: doc)
                await reselectTextBox(doc, pageIndex: sel.pageIndex, id: sel.id)
            } catch {
                statusMessage = String(localized: "Couldn't move that text.")
            }
        }
    }

    /// Opens the text field on the selected box so a typo can be corrected. The
    /// anchor handed to the edit is the box's bounds lower-left, not a tap point.
    func editSelectedTextBox() {
        guard let sel = selectedTextBox, !busy.isBlocked else { return }
        openTextEditor(on: sel)
    }

    private func openTextEditor(on sel: SelectedTextBox) {
        selectedTextBox = nil
        guard permits(capabilities.canAddText) else { return }
        draftText = sel.text
        draftSize = sel.fontSize
        draftFont = sel.fontName
        pendingText = PendingText(pageIndex: sel.pageIndex,
                                  x: sel.rect.left, y: sel.rect.bottom,
                                  editingId: sel.id, fontSize: sel.fontSize,
                                  fontName: sel.fontName, initialText: sel.text)
    }

    func removeSelectedTextBox() {
        guard let sel = selectedTextBox, let doc = document, !busy.isBlocked else { return }
        guard permits(capabilities.canAddText) else { return }
        guard let token = beginPageChange(sel.pageIndex, near: sel.rect) else { return }
        Task {
            defer { busy.end(token) }
            do {
                guard await confirmPageRewrite(doc, pageIndex: sel.pageIndex, token: token) else { return }
                // boundsAnchored: the coordinates are the box's reported rect, so
                // an undo must re-add against bounds, not the baseline.
                try await perform(
                    TextBoxOperation(pageIndex: sel.pageIndex, id: sel.id, text: sel.text,
                                     fontSize: sel.fontSize,
                                     x: sel.rect.left, y: sel.rect.bottom,
                                     adding: false, boundsAnchored: true,
                                     fontName: sel.fontName),
                    doc: doc)
            } catch {
                statusMessage = String(localized: "Couldn't remove that text.")
            }
        }
    }

    /// Before the first text-box change on a page (#139): a text box makes PDFium regenerate the
    /// page's content, which on some pages changes parts the person never touched. Such a change
    /// is never refused; the core's dry run says whether this page is one, and then the person is
    /// asked once. True to go ahead.
    ///
    /// #145: the check usually started earlier, in the background. The change reuses it (or starts
    /// one) and waits at most the budget, 1.5 s, with "Checking this page…" on the page. A page that
    /// keeps its look, can't be judged, or whose check ran past the budget is settled and the change
    /// applies without a prompt; Continue settles it too, Cancel applies nothing and leaves it to ask
    /// again next time. `token` is the change's busy work, which keeps every other edit waiting
    /// throughout, so there is never a second question.
    private func confirmPageRewrite(_ doc: PdfDocument, pageIndex: Int, token: BusyToken) async -> Bool {
        if !pageChecks.isSettled(pageIndex) {
            busy.update(token, label: .checkingPage, showsIndicator: true)
            switch await pageChecks.outcome(for: pageIndex) {
            case .abandoned:
                return false
            case .apply:
                break
            case .warn:
                // The document may have been closed or replaced while the check ran.
                guard document === doc else { return false }
                busy.update(token, showsIndicator: false)
                // Only one warning at a time: an older one still waiting counts as cancelled.
                answerPageRewriteWarning(false)
                let proceed = await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                    pageRewriteContinuation = continuation
                    pageRewriteWarning = PageRewriteWarning(pageIndex: pageIndex)
                }
                guard proceed, document === doc else { return false }
                pageChecks.settle(pageIndex)
            }
        }
        guard document === doc else { return false }
        busy.update(token, label: .applying, showsIndicator: true)
        return true
    }

    /// The warning's buttons: Continue (true) or Cancel (false). Safe to call with none showing.
    func answerPageRewriteWarning(_ proceed: Bool) {
        pageRewriteWarning = nil
        let continuation = pageRewriteContinuation
        pageRewriteContinuation = nil
        continuation?.resume(returning: proceed)
    }

    /// Re-reads the box after an edit and keeps it selected, so the handles stay
    /// on it. The rect must be read back rather than reused: correcting the text
    /// changes the box's width.
    private func reselectTextBox(_ doc: PdfDocument, pageIndex: Int, id: String) async {
        guard let box = try? await PdfEngine.shared.textBoxes(doc, pageIndex: pageIndex)
            .first(where: { $0.id == id }) else { return }
        selectedTextBox = SelectedTextBox(pageIndex: pageIndex, id: id, text: box.text,
                                          fontSize: box.fontSize, fontName: box.fontName,
                                          rect: box.rect)
    }

    // MARK: - undo / redo (#34)

    func undo() {
        guard let doc = document,
              let token = busy.begin(.applying, scope: .document, showsIndicator: false) else { return }
        Task {
            defer { busy.end(token) }
            do {
                if let operation = try await history.undo(PdfEngine.shared, doc) {
                    await afterHistoryChange(operation)
                }
            } catch {
                statusMessage = String(localized: "Couldn't undo that.")
            }
        }
    }

    func redo() {
        guard let doc = document,
              let token = busy.begin(.applying, scope: .document, showsIndicator: false) else { return }
        Task {
            defer { busy.end(token) }
            do {
                if let operation = try await history.redo(PdfEngine.shared, doc) {
                    await afterHistoryChange(operation)
                }
            } catch {
                statusMessage = String(localized: "Couldn't redo that.")
            }
        }
    }

    /// Applies an edit through the history and refreshes everything that depends
    /// on it. The single funnel for every reversible change.
    private func perform(_ operation: PdfEditOperation, doc: PdfDocument) async throws {
        // The backstop behind every gated entry point (#131): an open without the
        // permission never reaches the engine, even if a tool forgot to check.
        guard capabilities.allows(operation) else { throw PdfError.restricted }
        try await history.perform(operation, PdfEngine.shared, doc)
        await afterHistoryChange(operation)
    }

    private func afterHistoryChange(_ operation: PdfEditOperation) async {
        canUndo = history.canUndo
        canRedo = history.canRedo
        selectedStamp = nil
        selectedTextBox = nil
        guard operation.changesDocument else {
            // A mark (#329): nothing on disk changed, so the document is not unsaved, there
            // is nothing to re-render and no page check to run — only the overlay moved.
            // The mark's own state has to be re-read from the core, though, because a redo
            // took fresh ids.
            await refreshRedactionMarks()
            return
        }
        // Deliberately conservative: any history movement leaves the document
        // possibly different from the bytes on disk, so it stays dirty.
        noteDocumentChanged()
        invalidatePage(operation.pageIndex)
    }

    /// Every change to the document goes through here: it is unsaved, and a save already running
    /// must not mark it saved (D3, #145).
    func noteDocumentChanged() {
        editCount += 1
        isDirty = true
    }

    func invalidatePage(_ index: Int) {
        renderedWidths.removeValue(forKey: index)
        if let w = lastWindow { updateRenderWindow(first: w.first, last: w.last, widthPx: w.widthPx) }
    }

    // MARK: - search (#26)

    /// As-you-type search: brief debounce, then a whole-document scan for
    /// case-insensitive literal matches, aggregated into the flat match list.
    /// An empty term just clears the results. `debounce` is only turned off by
    /// the `-screenshot search` seeding, which supplies the whole term at once.
    func search(term: String, debounce: Bool = true) {
        searchTask?.cancel()
        endSearchBusy()
        searchMatches = []
        currentMatchIndex = nil
        guard !term.isEmpty, case let .viewing(_, pageSizes) = state,
              let doc = document else {
            isSearching = false
            return
        }
        isSearching = true
        searchTask = Task {
            if debounce {
                try? await Task.sleep(nanoseconds: 250_000_000)
                if Task.isCancelled { return }
            }
            // "Searching…" in the strip, which doesn't block editing (#145).
            let token = busy.begin(.searching, scope: .document, blocking: false)
            searchToken = token
            defer { if let token, searchToken == token { endSearchBusy() } }
            var matches: [SearchMatch] = []
            for index in 0..<pageSizes.count {
                if Task.isCancelled { return }
                let hits = (try? await PdfEngine.shared.search(
                    doc, pageIndex: index, term: term)) ?? []
                matches.append(contentsOf: hits.map {
                    SearchMatch(pageIndex: index, rects: $0.rects)
                })
            }
            if Task.isCancelled { return }
            searchMatches = matches
            currentMatchIndex = matches.isEmpty ? nil : 0
            isSearching = false
        }
    }

    func nextMatch() {
        guard let current = currentMatchIndex, !searchMatches.isEmpty else { return }
        currentMatchIndex = (current + 1) % searchMatches.count
    }

    func previousMatch() {
        guard let current = currentMatchIndex, !searchMatches.isEmpty else { return }
        currentMatchIndex = (current + searchMatches.count - 1) % searchMatches.count
    }

    /// Search bar dismissed: drop highlights and any in-flight scan.
    func clearSearch() {
        searchTask?.cancel()
        endSearchBusy()
        searchMatches = []
        currentMatchIndex = nil
        isSearching = false
    }

    private func endSearchBusy() {
        if let searchToken { busy.end(searchToken) }
        searchToken = nil
    }

    // MARK: - signatures (#22)

    /// Imports a picked photo: decode, contract cleanup, store.
    func importSignature(imageData: Data) {
        guard let ui = UIImage(data: imageData), let cg = ui.cgImage,
              var (pixels, w, h) = PixelBuffers.argbPixels(from: downscaled(cg))
        else {
            statusMessage = String(localized: "Couldn't decode that image.")
            return
        }
        if !SignatureProcessor.hasTransparency(pixels) {
            pixels = SignatureProcessor.removeWhiteBackground(pixels)
        }
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        (pixels, w, h) = (trimmed.pixels, trimmed.width, trimmed.height)
        storeSignature(pixels: pixels, width: w, height: h)
    }

    /// Stores a drawn signature: already transparent, so only trim applies.
    func addDrawnSignature(image: CGImage) {
        guard var (pixels, w, h) = PixelBuffers.argbPixels(from: image) else {
            statusMessage = String(localized: "Couldn't capture the drawing.")
            return
        }
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        (pixels, w, h) = (trimmed.pixels, trimmed.width, trimmed.height)
        storeSignature(pixels: pixels, width: w, height: h)
    }

    func deleteSignature(id: String) {
        signatureStore.delete(id: id)
        signatures = signatureStore.load()
    }

    func renameSignature(id: String, name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        signatureStore.rename(id: id, displayName: trimmed)
        signatures = signatureStore.load()
    }

    func startPlacement(_ entry: SignatureEntry) {
        guard !busy.isBlocked else { return }
        guard permits(capabilities.canSign) else { return }
        selectedTextBox = nil
        pendingSignature = entry
        statusMessage = String(localized: "Tap the page where the signature should go")
    }

    func cancelPlacement() {
        pendingSignature = nil
    }

    func commitStampRect(_ newRect: PdfRect) {
        guard let sel = selectedStamp, let doc = document,
              case let .viewing(_, pageSizes) = state else { return }
        guard !busy.isBlocked else { return }
        guard permits(capabilities.canSign) else { return }
        let rect = clampToPage(newRect, pageSize: pageSizes[sel.pageIndex])
        guard let token = busy.begin(.applying, scope: .page(sel.pageIndex, sel.rect)) else { return }
        Task {
            defer { busy.end(token) }
            do {
                let engine = PdfEngine.shared
                guard let image = try await engine.stampImage(
                    doc, pageIndex: sel.pageIndex, annotIndex: sel.annotIndex) else {
                    statusMessage = String(localized: "Couldn't read this signature's image.")
                    return
                }
                try await perform(
                    MoveStampOperation(pageIndex: sel.pageIndex, id: sel.id,
                                       pixels: image.pixels,
                                       pixelWidth: image.width, pixelHeight: image.height,
                                       from: sel.rect, to: rect),
                    doc: doc)
                // Keep the moved stamp selected so the handles stay put.
                if let placed = try await engine.stamps(doc, pageIndex: sel.pageIndex)
                    .first(where: { $0.id == sel.id }) {
                    selectedStamp = SelectedStamp(pageIndex: sel.pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: sel.id, rect: placed.rect)
                }
            } catch {
                statusMessage = String(localized: "Couldn't move the signature.")
            }
        }
    }

    func removeSelectedStamp() {
        guard let sel = selectedStamp, let doc = document, !busy.isBlocked else { return }
        guard permits(capabilities.canSign) else { return }
        guard let token = busy.begin(.applying, scope: .page(sel.pageIndex, sel.rect)) else { return }
        Task {
            defer { busy.end(token) }
            do {
                let engine = PdfEngine.shared
                // Read the image back first: without it, undo could not put the
                // same signature back.
                guard let image = try await engine.stampImage(
                    doc, pageIndex: sel.pageIndex, annotIndex: sel.annotIndex) else {
                    statusMessage = String(localized: "Couldn't remove this signature.")
                    return
                }
                try await perform(
                    StampOperation(pageIndex: sel.pageIndex, id: sel.id,
                                   pixels: image.pixels,
                                   pixelWidth: image.width, pixelHeight: image.height,
                                   rect: sel.rect, adding: false),
                    doc: doc)
            } catch {
                statusMessage = String(localized: "Couldn't remove this signature.")
            }
        }
    }

    func deselectStamp() {
        selectedStamp = nil
    }

    private func placeSignature(_ entry: SignatureEntry, doc: PdfDocument,
                                pageIndex: Int, pageSize: CGSize, x: Double, y: Double) {
        guard let image = signatureStore.loadImage(entry),
              let (pixels, w, h) = PixelBuffers.argbPixels(from: image) else {
            statusMessage = String(localized: "That signature's image is missing.")
            return
        }
        // Default size: a third of the page width, aspect preserved.
        var wPt = Double(pageSize.width) / 3.0
        var hPt = wPt * Double(h) / Double(w)
        let maxH = Double(pageSize.height) / 3.0
        if hPt > maxH {
            hPt = maxH
            wPt = hPt * Double(w) / Double(h)
        }
        let rect = clampToPage(
            PdfRect(left: x - wPt / 2, bottom: y - hPt / 2,
                    right: x + wPt / 2, top: y + hPt / 2),
            pageSize: pageSize)
        let id = "sig:\(UUID().uuidString)"
        guard let token = busy.begin(.applying, scope: .page(pageIndex, rect)) else { return }
        Task {
            defer { busy.end(token) }
            do {
                let engine = PdfEngine.shared
                try await perform(
                    StampOperation(pageIndex: pageIndex, id: id, pixels: pixels,
                                   pixelWidth: w, pixelHeight: h,
                                   rect: rect, adding: true),
                    doc: doc)
                if let placed = try await engine.stamps(doc, pageIndex: pageIndex)
                    .first(where: { $0.id == id }) {
                    selectedStamp = SelectedStamp(pageIndex: pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: id, rect: placed.rect)
                }
            } catch {
                statusMessage = String(localized: "Couldn't place the signature.")
            }
        }
    }

    private func storeSignature(pixels: [UInt32], width: Int, height: Int) {
        // A full library is a limit, not a failure, and `add` says no to it the same
        // way it says no to a write that failed (#333). Ask first, so the user is
        // told which of the two happened.
        guard !signatureStore.isFull else {
            statusMessage = String(localized: "The signature library is limited to \(SignatureStore.softLimit) signatures.")
            return
        }
        // The default name is localised once, at creation, and persisted as-is in
        // the signature store: it does not re-translate if the device language
        // changes later. Catalog key "Signature %lld".
        guard let image = PixelBuffers.image(from: pixels, width: width, height: height),
              let entry = signatureStore.add(
                  displayName: String(localized: "Signature \(signatures.count + 1)"),
                  image: image) else {
            statusMessage = String(localized: "Couldn't save the signature.")
            return
        }
        signatures.append(entry)
        statusMessage = String(localized: "Signature added")
    }

    private func downscaled(_ image: CGImage, maxDim: Int = 1500) -> CGImage {
        guard image.width > maxDim || image.height > maxDim else { return image }
        let scale = Double(maxDim) / Double(max(image.width, image.height))
        let w = max(Int(Double(image.width) * scale), 1)
        let h = max(Int(Double(image.height) * scale), 1)
        guard let ctx = CGContext(
            data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue |
                CGBitmapInfo.byteOrder32Little.rawValue) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    private func clampToPage(_ rect: PdfRect, pageSize: CGSize) -> PdfRect {
        let w = min(rect.right - rect.left, Double(pageSize.width))
        let h = min(rect.top - rect.bottom, Double(pageSize.height))
        let left = min(max(rect.left, 0), Double(pageSize.width) - w)
        let bottom = min(max(rect.bottom, 0), Double(pageSize.height) - h)
        return PdfRect(left: left, bottom: bottom, right: left + w, top: bottom + h)
    }

    // MARK: - save (#23)

    /// Save = write back to the opened document's URL. Same guarantees as the
    /// other platforms: serialize first, verify the output reopens in the
    /// engine, only then touch the destination (coordinated, atomic).
    ///
    /// #145: "Saving…", then "Checking the saved file…" in the strip; editing, Close and the file
    /// commands wait. The document is marked saved only if nothing changed while the save ran (D3).
    /// `then` is the unsaved-changes prompt's Save: close, share, or open a different document,
    /// once the save that just wrote this one has landed.
    func save(then followUp: UnsavedChangesFollowUp? = nil) {
        guard let doc = document, let url = sourceURL, !isSaving,
              let token = busy.begin(.saving, scope: .document, blocksFileCommands: true) else { return }
        isSaving = true
        let editsAtStart = editCount
        Task {
            defer {
                isSaving = false
                busy.end(token)
            }
            let staged = Self.stagingURL()
            defer { try? FileManager.default.removeItem(at: staged) }
            do {
                let engine = PdfEngine.shared
                try await engine.save(doc, to: staged)
                busy.update(token, label: .checkingSavedFile)
                let verify = try await engine.open(file: staged, like: doc)  // still protected if the document was (#132)
                await engine.close(verify)

                busy.update(token, label: .saving)
                try await Self.write(staged, over: url, readBy: doc)
                guard document === doc else { return }
                let unchanged = editCount == editsAtStart
                if unchanged { isDirty = false }
                switch (followUp, unchanged) {
                case (.close, true):
                    busy.end(token)
                    isSaving = false
                    close()
                case (.share, true):
                    shareURL = url
                case let (.open(newURL), true):
                    // openPicked begins its own busy token (#377) — it would find this one
                    // still blocking and refuse, the same reason `.close` above ends it first.
                    busy.end(token)
                    isSaving = false
                    openPicked(url: newURL)
                default:
                    statusMessage = String(localized: "Saved")
                    statusDetail = summaryForSave
                }
                summaryForSave = nil
            } catch {
                summaryForSave = nil
                showSaveFailed(error)
            }
        }
    }

    /// A new file in the temporary directory for a save to be staged in (#147).
    private nonisolated static func stagingURL() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("save-\(UUID().uuidString).pdf")
    }

    /// Writes an already-verified staged file over the opened file: security-scoped access,
    /// coordinated with any other writer, atomic. Save and the Password command share it; both
    /// verify `staged` before calling, so nothing unchecked reaches the file. Off the main
    /// actor, so a slow file provider doesn't freeze the screen.
    ///
    /// The staged file is mapped, not read, so a large document is not held in memory (#147).
    /// An atomic write replaces the file, which leaves `document` reading what it was opened on;
    /// in case a provider writes in place instead, the document first moves onto a copy of it,
    /// a clone on APFS that costs nothing. Should that copy fail, the atomic write goes ahead.
    private nonisolated static func write(_ staged: URL, over url: URL, readBy document: PdfDocument) async throws {
        if await PdfEngine.shared.reads(document, file: url) {
            try? await PdfEngine.shared.readFromCopy(document)
        }
        try await Task.detached(priority: .userInitiated) {
            let data = try Data(contentsOf: staged, options: .alwaysMapped)
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            var coordError: NSError?
            var writeError: Error?
            NSFileCoordinator().coordinate(
                writingItemAt: url, options: .forReplacing, error: &coordError
            ) { target in
                do { try data.write(to: target, options: .atomic) }
                catch { writeError = error }
            }
            if let error = coordError { throw error }
            if let error = writeError { throw error }
        }.value
    }

    private func showSaveFailed(_ error: Error) {
        // The OS/engine description follows as its own sentence; it is
        // already localised (PdfError is a LocalizedError).
        statusMessage = String(localized: "Save failed — use Save a copy.")
            + " " + error.localizedDescription
    }

    /// A serialized (and engine-verified) copy in a staged file for the Save-a-copy exporter,
    /// with the same busy state as Save (#145). Staged rather than in memory, so a large document
    /// exports without holding its size twice (#147); `finishExport` removes it. Nil when it
    /// failed, or when other work is still running.
    ///
    /// The staged file carries the document's name, in a folder of its own: the export sheet
    /// names the copy after the file it is handed, whatever its default name says, and a staged
    /// "save-<UUID>.pdf" was what every copy was being called (#278).
    func exportFile(named name: String) async -> URL? {
        guard let doc = document,
              let token = busy.begin(.saving, scope: .document, blocksFileCommands: true) else { return nil }
        defer { busy.end(token) }
        let editsAtStart = editCount
        discardExportFile()
        let staged: URL
        do {
            staged = try Self.namedStagingURL(for: name)
        } catch {
            summaryForSave = nil
            statusMessage = String(localized: "Couldn't prepare the copy.")
            return nil
        }
        do {
            let engine = PdfEngine.shared
            try await engine.save(doc, to: staged)
            busy.update(token, label: .checkingSavedFile)
            let verify = try await engine.open(file: staged, like: doc)  // still protected if the document was (#132)
            await engine.close(verify)
            guard document === doc else {
                try? FileManager.default.removeItem(at: staged)
                return nil
            }
            exportEditCount = editsAtStart
            exportStagedURL = staged
            return staged
        } catch {
            try? FileManager.default.removeItem(at: staged.deletingLastPathComponent())
            summaryForSave = nil
            statusMessage = String(localized: "Couldn't prepare the copy.")
            return nil
        }
    }

    /// `<tmp>/export-<UUID>/<name>.pdf`: a fresh folder, so the name can be the document's own.
    private nonisolated static func namedStagingURL(for name: String) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("export-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var base = name.replacingOccurrences(of: "/", with: "-")
        if base.lowercased().hasSuffix(".pdf") { base = String(base.dropLast(4)) }
        if base.isEmpty { base = String(localized: "Document") }
        return folder.appendingPathComponent(base).appendingPathExtension("pdf")
    }

    /// The exporter finished. When it wrote the copy, the document is marked saved, but only if
    /// nothing changed since the copy was made (D3). The staged file goes either way.
    func finishExport(saved: Bool) {
        discardExportFile()
        let summary = summaryForSave
        summaryForSave = nil
        guard saved else {
            // Cancelled: nothing was written, but the redaction was applied to the open
            // document, so what it removed is still said.
            if let summary { redactionSummary = summary }
            return
        }
        if exportEditCount == editCount { isDirty = false }
        exportEditCount = nil
        statusMessage = String(localized: "Saved")
        statusDetail = summary
    }

    private func discardExportFile() {
        if let staged = exportStagedURL {
            try? FileManager.default.removeItem(at: staged.deletingLastPathComponent())
            exportStagedURL = nil
        }
    }

    // MARK: - share (#378)

    /// Share hands over `sourceURL` itself, so it needs a real file behind the document —
    /// the demo and UI-test documents open from bytes and have none — and nothing else
    /// touching the file at the same time.
    var canShare: Bool { sourceURL != nil && !fileCommandsBlocked }

    /// Share (#378): the document exactly as it is on disk. Called directly when there are
    /// no unsaved changes, and by the alert's Discard button when there are — "share the
    /// document as currently saved on disk" is this same file either way, so there is only
    /// one implementation of it. The alert's Save button instead calls `save(then: .share)`,
    /// which sets `shareURL` to this same file once the save that just wrote it lands.
    func share() {
        guard let url = sourceURL else { return }
        shareURL = url
    }

    // MARK: - password command (#131)

    /// The Password command saves, so it needs a file to save to and nothing else in flight.
    var canUsePasswordCommand: Bool { sourceURL != nil && !isSaving && !isUnlocking && !busy.isBlocked }

    /// Opens the Password sheet in the mode this open's security calls for: without full
    /// access it can only explain and offer the owner password (ADR-004 §3).
    func showPasswordCommand() {
        guard document != nil, canUsePasswordCommand else { return }
        securityError = nil
        if !capabilities.canChangeSecurity {
            securitySheet = .unlock
        } else {
            securitySheet = security.isEncrypted ? .change : .set
        }
    }

    func showUnlock() {
        guard document != nil, capabilities.isRestricted, !isUnlocking else { return }
        securityError = nil
        securitySheet = .unlock
    }

    func dismissSecuritySheet() {
        securitySheet = nil
        securityError = nil
    }

    /// Reopens a restricted document with its owner password, for full access (ADR-004 §3).
    /// The bytes are the document as it stands, its security kept, so the demo and any
    /// change its restrictions did allow come along. A wrong password keeps the sheet open
    /// and says so; nothing about the open document changes until the password is right.
    func unlock(_ ownerPassword: String) {
        guard let doc = document, !isUnlocking, !isSaving,
              case let .viewing(displayName, _) = state,
              let token = busy.begin(.opening, scope: .document, blocksFileCommands: true) else { return }
        securityError = nil
        isUnlocking = true
        Task {
            defer {
                isUnlocking = false
                busy.end(token)
            }
            let engine = PdfEngine.shared
            let wrong = String(localized: "That password didn't work. Try again.")
            // The document as it stands, staged in a file (#147); the unlocked document reads it,
            // and its name goes once that document has it open.
            let staged = Self.stagingURL()
            defer { try? FileManager.default.removeItem(at: staged) }
            do {
                try await engine.save(doc, to: staged)
                let trial: PdfDocument
                do {
                    trial = try await engine.open(file: staged, password: ownerPassword)
                } catch PdfError.passwordRequired {
                    securityError = wrong
                    return
                }
                let trialSecurity = await engine.security(trial)
                await engine.close(trial)
                // A user password opens it as well, but only with the restrictions.
                guard trialSecurity.hasFullAccess else {
                    securityError = wrong
                    return
                }
                guard document === doc else { return }
                let wasDirty = isDirty
                let url = sourceURL
                securitySheet = nil
                await open(source: .file(staged), password: ownerPassword, displayName: displayName, sourceURL: url)
                guard case .viewing = state else { return }
                // The reopen starts clean; edits made before the unlock are still unsaved.
                isDirty = wasDirty
                showNotice(String(localized: "Document unlocked."))
            } catch {
                securityError = String(localized: "Couldn't unlock this document.")
            }
        }
    }

    /// Sets or changes the password: one password that opens the document with every
    /// permission, AES-256 (ADR-004 §4 and §5).
    func setPassword(_ newPassword: String) {
        changeSecurity(to: newPassword)
    }

    /// Takes the document's security off.
    func removePassword() {
        changeSecurity(to: nil)
    }

    /// Setting, changing and removing security is a save (ADR-004 §6): serialize with the
    /// new security, check the copy opens with the new password (or with none), write it
    /// over the file exactly as Save does, then reopen what was written, so the open
    /// document, its credentials and its permissions match what is on disk. Unsaved
    /// changes are part of that save.
    private func changeSecurity(to newPassword: String?) {
        guard let doc = document, let url = sourceURL, !isSaving, !isUnlocking,
              case let .viewing(displayName, _) = state else { return }
        guard capabilities.canChangeSecurity else {
            // The sheet only offers this with full access, and the core refuses it too.
            securitySheet = nil
            showRestrictedNotice()
            return
        }
        // Editing, Close and the file commands wait while it runs (#145).
        guard let token = busy.begin(.saving, scope: .document, blocksFileCommands: true) else { return }
        let wasEncrypted = security.isEncrypted
        isSaving = true
        Task {
            defer {
                isSaving = false
                busy.end(token)
            }
            let engine = PdfEngine.shared
            let staged = Self.stagingURL()
            defer { try? FileManager.default.removeItem(at: staged) }
            do {
                if let newPassword {
                    try await engine.save(doc, userPassword: newPassword, ownerPassword: nil,
                                          permissions: .all, to: staged)
                    busy.update(token, label: .checkingSavedFile)
                    let verify = try await engine.open(file: staged, password: newPassword)
                    await engine.close(verify)
                } else {
                    try await engine.saveWithoutSecurity(doc, to: staged)
                    busy.update(token, label: .checkingSavedFile)
                    let verify = try await engine.open(file: staged)
                    await engine.close(verify)
                }
                busy.update(token, label: .saving)
                try await Self.write(staged, over: url, readBy: doc)
                guard document === doc else { return }
                securitySheet = nil
                busy.update(token, label: .opening)
                // What was written, from the staged file it was written from (#147).
                await open(source: .file(staged), password: newPassword, displayName: displayName, sourceURL: url)
                guard case .viewing = state else { return }
                if newPassword == nil {
                    showNotice(String(localized: "Password removed."))
                } else if wasEncrypted {
                    showNotice(String(localized: "Password changed."))
                } else {
                    showNotice(String(localized: "Password set."))
                }
            } catch {
                // An alert can't show over the sheet, so the sheet goes first.
                securitySheet = nil
                showSaveFailed(error)
            }
        }
    }

    // MARK: - closing

    func close() {
        // Not while a save, a password change or a change being applied still needs the document.
        guard !closeBlocked else { return }
        closeCurrent()
        state = .home(recents: recents.load(), error: nil)
        refreshRecents()
    }

    private func toHome(_ error: String) {
        state = .home(recents: recents.load(), error: error)
        refreshRecents()
    }

    private func closeCurrent() {
        renderTask?.cancel()
        pendingBodyEdit = nil
        scannedHintShown = false
        clearSearch()
        pageImages = [:]
        renderedWidths = [:]
        lastWindow = nil
        sourceURL = nil
        if let scoped = scopedAccessURL {
            scoped.stopAccessingSecurityScopedResource()
            scopedAccessURL = nil
        }
        discardExportFile()
        isDirty = false
        pendingSignature = nil
        selectedStamp = nil
        selectedTextBox = nil
        // History belongs to the open document — never offer to undo an edit
        // made to a file that is no longer on screen.
        history.clear()
        canUndo = false
        canRedo = false
        // The warning memory is the open document's too (#139); a warning still up is a Cancel.
        answerPageRewriteWarning(false)
        // Every page check stops with the document, and nothing about its pages is remembered (#145).
        pageChecks.reset()
        pageShownTask?.cancel()
        pageShownTask = nil
        currentPage = nil
        exportEditCount = nil
        busy.endPageWork()
        isPlacingText = false
        pendingText = nil
        draftText = ""
        // Security is the open document's; the next one reads its own (#131).
        security = .unprotected
        securitySheet = nil
        securityError = nil
        // Marks are the open document's too (#329, ADR-005 decision 1): they live in the
        // core and are never written, so what is on screen after this belongs to the core
        // that is about to be closed. Leaving the map up drew the previous file's marks
        // over the next one at the same page indices — and, because the count is derived
        // from it, made Save ask about redacting a document that had none.
        redactionMarks = [:]
        selectedRedactionMark = nil
        redactMode = false
        redactionSummary = nil
        redactionRefusal = nil
        summaryForSave = nil
        if let doc = document {
            document = nil
            Task { await PdfEngine.shared.close(doc) }
        }
    }
}
