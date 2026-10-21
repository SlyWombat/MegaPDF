import SwiftUI

/// The editor for a PDF form's own text field (#701): a sheet on iPhone and iPad alike,
/// prefilled with the field's value, the keyboard up, Cancel and Done. The same shape as
/// `BodyTextSheet`.
///
/// What a return does is the field's own (#712). A **multi-line** field is a vertical-axis
/// text field, so a return is a new line. A **single-line** field takes no returns at all:
/// the keyboard's key says Done and submits, as the Done button does, which is what every
/// other single-line field on iOS does and what the form's author meant by not setting the
/// multi-line flag. A `/MaxLen` stops the typing at the field's own limit.
struct FormTextSheet: View {
    @Binding var text: String
    let fieldName: String
    var isMultiline = false
    /// The field's own limit in characters; 0 is none.
    var maxLength = 0
    let onDone: () -> Void
    let onCancel: () -> Void

    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    field
                        .autocorrectionDisabled()
                        .focused($focused)
                        .accessibilityIdentifier("formTextField")
                        .onChange(of: text) { newValue in
                            if let limited = FormTextLimit.apply(maxLength, to: newValue), limited != newValue {
                                text = limited
                            }
                        }
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

    @ViewBuilder
    private var field: some View {
        if isMultiline {
            TextField("Text", text: $text, axis: .vertical)
        } else {
            TextField("Text", text: $text)
                .submitLabel(.done)
                .onSubmit(onDone)
        }
    }
}

/// A form field's `/MaxLen` as a rule on the draft (#712). Pure, so the cut is tested.
enum FormTextLimit {
    /// `text` cut to `maxLength` characters, or nil when there is no limit (`maxLength` <= 0).
    static func apply(_ maxLength: Int, to text: String) -> String? {
        guard maxLength > 0 else { return nil }
        return String(text.prefix(maxLength))
    }
}
