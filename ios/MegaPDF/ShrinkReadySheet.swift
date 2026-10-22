import SwiftUI

/// The result of Shrink for email (#593): "Was X, now Y", the reassurance that the open document
/// is unchanged, then Share (the emailing case, first) and Save to Files. Done closes it and the
/// staged copy goes.
///
/// `ShareLink` rather than the app's `ShareSheet`: it is iOS 16 (the deployment target) and
/// places its own iPad popover, which `ShareSheet` needs a hand-measured anchor for.
///
/// Save to Files does not present the exporter itself: it hands the copy over and closes this
/// sheet, and the viewer presents the one file exporter (#589) from the sheet's `onDismiss`.
/// Presenting it while this sheet is still animating away fails silently, which is #589's
/// family of bug.
struct ShrinkReadySheet: View {
    let ready: ShrinkReady
    let onSaveToFiles: () -> Void
    let onDone: () -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("Was \(Self.size(ready.before)), now \(Self.size(ready.after)).")
                        .accessibilityIdentifier("shrinkReadySizes")
                } footer: {
                    Text("The document you have open is unchanged.")
                }
                Section {
                    ShareLink(item: ready.url) {
                        Label("Share", systemImage: "square.and.arrow.up")
                    }
                    .accessibilityIdentifier("shrinkShare")
                    Button("Save to Files", action: onSaveToFiles)
                        .accessibilityIdentifier("shrinkSaveToFiles")
                }
            }
            .navigationTitle("Smaller copy ready")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done", action: onDone)
                        .accessibilityIdentifier("shrinkDone")
                }
            }
        }
        .presentationDetents([.medium])
    }

    /// The platform's own file-size wording, localised ("12,4 Mo" in French).
    private static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
