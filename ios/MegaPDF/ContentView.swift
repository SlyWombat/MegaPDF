import SwiftUI
import UniformTypeIdentifiers

/// Wraps a staged, serialized PDF for the Save-a-copy file exporter. The file is handed over
/// as it is, not read into memory, so a large document exports like a small one (#147).
struct PdfExportDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.pdf]
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

/// The system's own Markdown UTI: declared by the OS already (Shortcuts, Notes import, and
/// others), so MegaPDF needs no `UTExportedTypeDeclarations` entry of its own for it. `.plainText`
/// is the fallback only if a future OS ever drops it, so the export sheet still gets a real type.
let megapdfMarkdownUTType: UTType = UTType("net.daringfireball.markdown") ?? .plainText

/// Wraps a staged Markdown export for the same file exporter shape as `PdfExportDocument`
/// (#386) -- a one-way, lossy text export, not another Save-a-copy format; see
/// `ViewerModel.exportMarkdownFile`.
struct MarkdownExportDocument: FileDocument {
    static let readableContentTypes: [UTType] = [megapdfMarkdownUTType]
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

struct ContentView: View {
    @StateObject private var model = ViewerModel()
    @State private var password = ""
    @State private var exporting = false
    @State private var exportDoc: PdfExportDocument?
    // Default file name for Save a copy until a document is open.
    @State private var exportName = String(localized: "Document")
    @State private var exportingMarkdown = false
    @State private var markdownExportDoc: MarkdownExportDocument?
    @State private var markdownExportName = String(localized: "Document")

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
                    onSaveCopy: {
                        // Repeat taps are ignored while the copy is prepared (#145).
                        guard !model.fileCommandsBlocked else { return }
                        exportName = displayName
                        Task {
                            if let file = await model.exportFile(named: displayName) {
                                exportDoc = PdfExportDocument(file: file)
                                exporting = true
                            }
                        }
                    },
                    onExportMarkdown: {
                        guard !model.fileCommandsBlocked else { return }
                        var base = displayName
                        if base.lowercased().hasSuffix(".pdf") { base = String(base.dropLast(4)) }
                        markdownExportName = base + ".md"
                        Task {
                            if let file = await model.exportMarkdownFile(named: displayName) {
                                markdownExportDoc = MarkdownExportDocument(file: file)
                                exportingMarkdown = true
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
        .fileExporter(
            isPresented: $exporting,
            document: exportDoc,
            contentType: .pdf,
            defaultFilename: exportName
        ) { result in
            if case .success = result {
                model.finishExport(saved: true)
            } else {
                model.finishExport(saved: false)
            }
        }
        // #386: a second, independent file exporter for the Markdown export -- alongside the
        // PDF one above rather than a shared picker, the same "second file-type choice" every
        // platform's Save As gets, in this app's own existing per-format-exporter shape.
        .fileExporter(
            isPresented: $exportingMarkdown,
            document: markdownExportDoc,
            contentType: megapdfMarkdownUTType,
            defaultFilename: markdownExportName
        ) { result in
            if case .success = result {
                model.finishMarkdownExport(saved: true)
            } else {
                model.finishMarkdownExport(saved: false)
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
