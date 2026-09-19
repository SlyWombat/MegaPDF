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

struct ContentView: View {
    @StateObject private var model = ViewerModel()
    @State private var password = ""
    @State private var exporting = false
    @State private var exportDoc: PdfExportDocument?
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
                    onClose: model.close
                )
            }
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
