import SwiftUI

/// The editor for a PDF form's own text field (#701): a sheet on iPhone and iPad alike,
/// prefilled with the field's value, the keyboard up, Cancel and Done. The same shape as
/// `BodyTextSheet`. A vertical-axis field so a multi-line field can take a return; PDFium
/// decides what a single-line field does with one.
struct FormTextSheet: View {
    @Binding var text: String
    let fieldName: String
    let onDone: () -> Void
    let onCancel: () -> Void

    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Text", text: $text, axis: .vertical)
                        .autocorrectionDisabled()
                        .focused($focused)
                        .accessibilityIdentifier("formTextField")
                } footer: {
                    // The field's own name, as the form's author wrote it: not translated.
                    Text(verbatim: fieldName)
                }
            }
            .navigationTitle("Form field")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: onCancel)
                        .accessibilityIdentifier("formTextCancel")
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done", action: onDone)
                        .accessibilityIdentifier("formTextDone")
                }
            }
            .onAppear { focused = true }
        }
        .presentationDetents([.medium, .large])
    }
}
