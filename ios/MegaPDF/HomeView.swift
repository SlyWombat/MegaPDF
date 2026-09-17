import SwiftUI
import UniformTypeIdentifiers

/// One row of the Recent list: the file name, and under it where the file lives (#165).
///
/// Every row carries its location, not only the rows whose names clash — files from
/// one template or one scanner share a name, and a list that added the folder only
/// sometimes would rearrange itself as entries came and went. A row whose location
/// is not known yet (stored before #165, and not yet filled in from its bookmark)
/// falls back to the date it last showed, so the second line is never blank.
struct RecentRow: View {
    let entry: RecentEntry
    let unavailable: Bool

    var body: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.displayName)
                    .foregroundStyle(unavailable ? AnyShapeStyle(.secondary) : AnyShapeStyle(.primary))
                    .lineLimit(1)
                    .truncationMode(.middle)
                subtitle
                    .font(Brand.Text.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    // The innermost folder is the part that tells two "agreement.pdf"
                    // rows apart, so it is the part a narrow screen keeps.
                    .truncationMode(.head)
            }
            Spacer(minLength: 0)
            if unavailable {
                Image(systemName: "exclamationmark.triangle")
                    .font(Brand.Text.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
        }
        // One element reading name-then-place, rather than three VoiceOver stops
        // per row; the label is set by the caller (#2).
        .accessibilityElement(children: .ignore)
    }

    @ViewBuilder
    private var subtitle: some View {
        if let location = entry.location {
            // "Not found ·" in words as well as in grey: colour alone is not a
            // statement anyone can hear, and the Windows half says the same thing.
            if unavailable {
                Text("Not found · \(location.subtitle)",
                     comment: "#165: a recent document that is no longer where it was")
            } else {
                Text(location.subtitle)
            }
        } else if unavailable {
            Text("Not found", comment: "#165: a recent document that is no longer where it was")
        } else {
            Text(Date(timeIntervalSince1970: Double(entry.lastOpenedEpochMs) / 1000),
                 style: .date)
        }
    }
}

struct HomeView: View {
    let recents: [RecentEntry]
    let unavailableRecentIDs: Set<String>
    let error: String?
    let onOpen: (URL) -> Void
    let onRecent: (RecentEntry) -> Void
    let onRemoveRecent: (RecentEntry) -> Void
    let onShowInFiles: (RecentEntry) -> Void

    @State private var importing = false
    @State private var aboutOpen = false

    var body: some View {
        VStack(spacing: 12) {
            Spacer().frame(height: 48)
            Text("MegaPDF").font(Brand.Text.title.bold())
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
                            let unavailable = unavailableRecentIDs.contains(entry.id)
                            Button {
                                onRecent(entry)
                            } label: {
                                RecentRow(entry: entry, unavailable: unavailable)
                            }
                            .accessibilityLabel(entry.accessibilityLabel(available: !unavailable))
                            // There is no hover on iOS and no pointer to rely on an
                            // iPad either, so the details live where iOS puts them:
                            // behind a long press (#165).
                            .contextMenu {
                                Button {
                                    onShowInFiles(entry)
                                } label: {
                                    Label("Show in Files", systemImage: "folder")
                                }
                                Button(role: .destructive) {
                                    onRemoveRecent(entry)
                                } label: {
                                    Label("Remove from Recents", systemImage: "trash")
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
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                Button {
                    aboutOpen = true
                } label: {
                    Image(systemName: "info.circle")
                }
                .accessibilityLabel("About MegaPDF")
            }
        }
        .sheet(isPresented: $aboutOpen) {
            AboutView()
        }
    }
}
