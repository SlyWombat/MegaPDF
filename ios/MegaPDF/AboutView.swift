import SwiftUI

/// About sheet: app name, version, copyright, credits, project link, and a
/// push to the bundled third-party license notices.
struct AboutView: View {
    @Environment(\.dismiss) private var dismiss

    private var versionLabel: String {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "—"
        guard let build = info?["CFBundleVersion"] as? String else {
            return "Version \(version)"
        }
        return "Version \(version) (\(build))"
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(spacing: 4) {
                        Text("MegaPDF").font(.title2.bold())
                        Text(versionLabel)
                            .font(.callout).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity)
                    .listRowBackground(Color.clear)
                }
                Section {
                    Text("Copyright © 2026 ElectricRV.ca Corporation. All rights reserved.")
                        .font(Brand.Text.caption).foregroundStyle(.secondary)
                    Text("Special thanks to Mega Woman.")
                        .font(Brand.Text.caption).foregroundStyle(.secondary)
                }
                Section {
                    Link("MegaPDF on GitHub",
                         destination: URL(string: "https://github.com/SlyWombat/MegaPDF")!)
                    NavigationLink("Third-party notices") {
                        ThirdPartyNoticesView()
                    }
                }
            }
            .navigationTitle("About")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Close") { dismiss() }
                }
            }
        }
    }
}

/// Full text of the bundled THIRD-PARTY-NOTICES.txt (~150 KB), loaded off
/// the main thread on first appearance.
struct ThirdPartyNoticesView: View {
    @State private var notices: String?

    var body: some View {
        Group {
            if let notices {
                ScrollView {
                    Text(notices)
                        .font(.caption.monospaced())
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding()
                }
            } else {
                ProgressView()
            }
        }
        .navigationTitle("Third-party notices")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            guard notices == nil else { return }
            notices = await Self.loadNotices()
        }
    }

    private static func loadNotices() async -> String {
        let text = await Task.detached(priority: .userInitiated) { () -> String? in
            guard let url = Bundle.main.url(forResource: "THIRD-PARTY-NOTICES",
                                            withExtension: "txt") else { return nil }
            return try? String(contentsOf: url, encoding: .utf8)
        }.value
        return text ?? "The notices file is missing from this build."
    }
}
