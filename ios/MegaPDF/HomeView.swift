import SwiftUI
import UniformTypeIdentifiers

struct HomeView: View {
    let recents: [RecentEntry]
    let error: String?
    let onOpen: (URL) -> Void
    let onRecent: (RecentEntry) -> Void

    @State private var importing = false

    var body: some View {
        VStack(spacing: 12) {
            Spacer().frame(height: 48)
            Text("MegaPDF").font(.largeTitle.bold())
            Text("Open. Fix. Save. Done.").foregroundStyle(.secondary)
            Button("Open PDF") { importing = true }
                .buttonStyle(.borderedProminent)
                .padding(.top, 12)

            if let error {
                Text(error).foregroundStyle(.red).font(.callout)
                    .multilineTextAlignment(.center).padding(.horizontal)
            }

            if !recents.isEmpty {
                List {
                    Section("Recent") {
                        ForEach(recents) { entry in
                            Button {
                                onRecent(entry)
                            } label: {
                                VStack(alignment: .leading) {
                                    Text(entry.displayName).foregroundStyle(.primary)
                                    Text(Date(timeIntervalSince1970: Double(entry.lastOpenedEpochMs) / 1000),
                                         style: .date)
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
                .listStyle(.insetGrouped)
            } else {
                Spacer()
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.pdf]) { result in
            if case let .success(url) = result { onOpen(url) }
        }
    }
}
