import SwiftUI

/// The inline editor for a line of the document's own text (#113). One field and
/// nothing else: the line keeps its size and font (SDD §3.1 — no formatting
/// controls), and clearing the field removes the line.
struct BodyTextSheet: View {
    @Binding var text: String
    let onSave: () -> Void
    let onCancel: () -> Void

    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Text", text: $text, axis: .vertical)
                        .autocorrectionDisabled()
                        .focused($focused)
                        .accessibilityIdentifier("bodyTextField")
                } footer: {
                    Text("Change this line. The size and font stay as they are.")
                }
            }
            .navigationTitle("Edit text")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: onCancel)
                        .accessibilityIdentifier("bodyTextCancel")
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save", action: onSave)
                        .accessibilityIdentifier("bodyTextSave")
                }
            }
            .onAppear { focused = true }
        }
        // .large as well as .medium (#167), for the same reason as the add-text
        // sheet: a half-height sheet is not enough at an accessibility text size,
        // and one detent leaves no way to grow it.
        .presentationDetents([.medium, .large])
    }
}

/// A one-line, non-modal notice that sits over the page and goes away on its own —
/// for things the user should know but need not act on, like a substituted font.
struct NoticeBanner: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.footnote)
            .multilineTextAlignment(.center)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(.regularMaterial, in: Capsule())
            .shadow(radius: 4, y: 2)
            .padding(.horizontal, 16)
            .accessibilityIdentifier("noticeBanner")
            .accessibilityAddTraits(.updatesFrequently)
    }
}
