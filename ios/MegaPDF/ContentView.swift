import SwiftUI
import UniformTypeIdentifiers

/// The system's own Markdown UTI: declared by the OS already (Shortcuts, Notes import, and
/// others), so MegaPDF needs no `UTExportedTypeDeclarations` entry of its own for it. `.plainText`
/// is the fallback only if a future OS ever drops it, so the export sheet still gets a real type.
let megapdfMarkdownUTType: UTType = UTType("net.daringfireball.markdown") ?? .plainText

/// Wraps an already-staged file for a `.fileExporter`. The file is handed over as it is, not
/// read into memory, so a large document exports like a small one (#147).
///
/// One type for both kinds of export rather than one per format (#589). SwiftUI's exporter
/// takes a single `FileDocument` type and a single content type, so a type per format meant a
/// modifier per format — and two `.fileExporter` modifiers on one view is not something
/// SwiftUI supports: only the last one attached ever presents. What is being exported is now
/// an `ExportKind` the one exporter reads, and both UTIs are declared here so either kind can
/// travel in this wrapper.
struct StagedExportDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.pdf, megapdfMarkdownUTType]
    var file: URL?
    var data: Data?

    init(file: URL) { self.file = file }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        if let file { return try FileWrapper(url: file, options: []) }
        return FileWrapper(regularFileWithContents: data ?? Data())
    }
}

/// Which export the single file exporter is presenting for, and so which content type the
/// sheet opens with and which completion the result goes to (#589).
enum ExportKind {
    /// Save a copy: a full PDF, which becomes the open document when it is written (#572).
    case pdf
    /// Export as Markdown: a one-way, lossy text export (#386), never a save of the document.
    case markdown

    var contentType: UTType {
        switch self {
        case .pdf: return .pdf
        case .markdown: return megapdfMarkdownUTType
        }
    }
}

struct ContentView: View {
    @StateObject private var model = ViewerModel()
    @State private var password = ""
    // One exporter's worth of state (#589): whether the sheet is up, what kind of export it
    // is for, the staged file it is handing over, and the name offered for it.
    @State private var exporting = false
    @State private var exportKind: ExportKind = .pdf
    @State private var exportDoc: StagedExportDocument?
    // Default file name for Save a copy until a document is open.
    @State private var exportName = String(localized: "Document")

    var body: some View {
        NavigationStack {
            switch model.state {
            case let .home(recents, error):
                HomeView(
                    recents: recents,
                    unavailableRecentIDs: model.unavailableRecentIDs,
                    error: error,
                    onOpen: model.openPicked,
                    onRecent: model.openRecent,
                    onRemoveRecent: { model.removeRecent(id: $0.id) },
                    onShowInFiles: model.showRecentInFiles
                )

            case .loading:
                OpeningView(busy: model.busy)

            case let .passwordNeeded(_, displayName, _, wrongPassword):
                VStack {
                    Text(displayName).font(Brand.Text.subtitle)
                }
                .alert("Password required", isPresented: .constant(true)) {
                    SecureField("Password", text: $password)
                    Button("Open") {
                        model.submitPassword(password)
                        password = ""
                    }
                    Button("Cancel", role: .cancel) {
                        password = ""
                        model.close()
                    }
                } message: {
                    // Two literals, not a ternary, so the catalog sees both.
                    if wrongPassword {
                        Text("That password didn't work. Try again.")
                    } else {
                        Text("This document is protected.")
                    }
                }

            case let .viewing(displayName, pageSizes):
                ViewerView(
                    model: model,
                    busy: model.busy,
                    displayName: displayName,
                    pageSizes: pageSizes,
                    onSaveCopy: { removeSignature in
                        // Repeat taps are ignored while the copy is prepared (#145).
                        guard !model.fileCommandsBlocked else { return }
                        Task {
                            if let file = await model.exportFile(named: displayName, removeSignature: removeSignature) {
                                // Kind, document and name are all set before `exporting`, in
                                // one hop on the main actor, so the render that presents the
                                // sheet already knows it is presenting for a PDF (#589).
                                exportKind = .pdf
                                exportName = displayName
                                exportDoc = StagedExportDocument(file: file)
                                exporting = true
                            }
                        }
                    },
                    onExportMarkdown: {
                        guard !model.fileCommandsBlocked else { return }
                        var base = displayName
                        if base.lowercased().hasSuffix(".pdf") { base = String(base.dropLast(4)) }
                        Task {
                            if let file = await model.exportMarkdownFile(named: displayName) {
                                exportKind = .markdown
                                exportName = base + ".md"
                                exportDoc = StagedExportDocument(file: file)
                                exporting = true
                            }
                        }
                    },
                    onClose: model.close
                )
            }
        }
        // #377: MegaPDF now declares CFBundleDocumentTypes, so the OS can hand it a PDF this
        // way — Mail's attachment viewer, "Open In…"/"Copy to MegaPDF" from another app's
        // share sheet, or a long-press "Open with" in Files. Routed through `openExternal`,
        // not `openPicked` directly: unlike the in-app picker (reachable only from Home, with
        // nothing open yet), this can arrive while a document with unsaved edits is already
        // open, and that needs the same "Unsaved changes" ask Close and Share already give.
        .onOpenURL { url in
            model.openExternal(url: url)
        }
        // **One** file exporter, for both Save a copy and Export as Markdown (#589).
        //
        // There were two, one per format, each with its own flag and its own document type
        // (#386 added the Markdown one). Two `.fileExporter` modifiers on one view is not a
        // thing SwiftUI supports: with both attached only the later one ever presented, so
        // Save a copy staged its copy, set its flag, and no sheet came up — on every
        // document, with no error, which is the worst way for an operation to fail. The
        // Markdown export worked, being the one attached last, which is why the UI test for
        // it passed throughout.
        //
        // So what is being exported is a state value rather than a choice of modifier:
        // `exportKind` says which sheet this is, and the result goes to that kind's
        // completion. The alternative — keeping both modifiers and attaching only the one
        // the pending export calls for — would leave the same two-exporter shape in the file
        // for the next person to re-stack, and it makes the fix depend on an `if` in the view
        // hierarchy flipping before the sheet is asked for. One exporter cannot regress that
        // way. (It is also what the desktops do: one Save As picker, the kind read back off
        // the path the picker returned — `SaveAsExport.KindForPath`.)
        .fileExporter(
            isPresented: $exporting,
            document: exportDoc,
            contentType: exportKind.contentType,
            defaultFilename: exportName
        ) { result in
            switch exportKind {
            case .pdf:
                // #572: where the sheet put the copy, not merely that it did. The result is
                // the only place that URL exists, and the model needs it: the copy becomes
                // the open document, so Save from here on writes to it rather than to the
                // file that was opened — which holds none of these edits.
                switch result {
                case let .success(url): model.finishExport(savedTo: url)
                case .failure: model.finishExport(savedTo: nil)
                }
            case .markdown:
                // A one-way text export: it reports "Exported" and never clears the unsaved
                // state a real Save still has to answer for (#386).
                if case .success = result {
                    model.finishMarkdownExport(saved: true)
                } else {
                    model.finishMarkdownExport(saved: false)
                }
            }
        }
        .alert(
            model.statusMessage ?? "",
            isPresented: Binding(
                get: { model.statusMessage != nil },
                set: { if !$0 { model.statusMessage = nil; model.statusDetail = nil } })
        ) {
            Button("OK") { model.statusMessage = nil; model.statusDetail = nil }
        } message: {
            if let detail = model.statusDetail { Text(detail) }
        }
    }
}

#Preview {
    ContentView()
}
