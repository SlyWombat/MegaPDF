import SwiftUI
import UniformTypeIdentifiers

/// Wraps serialized PDF bytes for the Save-a-copy file exporter.
struct PdfExportDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.pdf]
    var data: Data

    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

struct ContentView: View {
    @StateObject private var model = ViewerModel()
    @State private var password = ""
    @State private var exporting = false
    @State private var exportDoc: PdfExportDocument?
    @State private var exportName = "Document"

    var body: some View {
        NavigationStack {
            switch model.state {
            case let .home(recents, error):
                HomeView(
                    recents: recents,
                    error: error,
                    onOpen: model.openPicked,
                    onRecent: model.openRecent
                )

            case .loading:
                ProgressView()

            case let .passwordNeeded(_, displayName, _, wrongPassword):
                VStack {
                    Text(displayName).font(.headline)
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
                    Text(wrongPassword
                        ? "That password didn't work. Try again."
                        : "This document is protected.")
                }

            case let .viewing(displayName, pageSizes):
                ViewerView(
                    model: model,
                    displayName: displayName,
                    pageSizes: pageSizes,
                    onSaveCopy: {
                        exportName = displayName
                        Task {
                            if let data = await model.exportData() {
                                exportDoc = PdfExportDocument(data: data)
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
            if case .success = result { model.markSavedCopy() }
        }
        .alert(
            model.statusMessage ?? "",
            isPresented: Binding(
                get: { model.statusMessage != nil },
                set: { if !$0 { model.statusMessage = nil } })
        ) {
            Button("OK") { model.statusMessage = nil }
        }
    }
}

#Preview {
    ContentView()
}
