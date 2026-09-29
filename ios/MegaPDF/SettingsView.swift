import SwiftUI

/// The app's preferences (#512).
///
/// The first one MegaPDF's phones have had: before reading mode there was nothing to
/// prefer, and the two things there are now to prefer are two scalars, so they are bound
/// straight to `@AppStorage` rather than given a JSON file of their own beside recents and
/// signatures (`ReadingDefaults` says why at more length). A grouped `List` in a sheet,
/// reached from the More menu with a document open and from the Home screen without one —
/// iOS's own shape for a small settings screen, and the same sheet both times.
struct SettingsView: View {
    @Environment(\.dismiss) private var dismiss

    @AppStorage(ReadingDefaults.pageColoursKey)
    private var pageColours: String = PageTint.normal.rawValue

    @AppStorage(ReadingDefaults.openInReadingModeKey)
    private var openInReadingMode: Bool = false

    /// `@AppStorage` stores the string the desktops store; the picker selects a tint. An
    /// unknown value shows as Normal rather than an empty picker, which is the same
    /// forgiveness `ReadingDefaults.pageTint` gives the reader.
    private var tint: Binding<PageTint> {
        Binding(get: { PageTint(rawValue: pageColours) ?? .normal },
                set: { pageColours = $0.rawValue })
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Picker("Page colours", selection: tint) {
                        ForEach(PageTint.allCases) { value in
                            Text(value.pickerLabel).tag(value)
                        }
                    }
                    .accessibilityIdentifier("settingsPageColours")
                    Toggle("Open documents in reading mode", isOn: $openInReadingMode)
                        .accessibilityIdentifier("settingsOpenInReadingMode")
                } header: {
                    Text("Reading")
                } footer: {
                    // Said here because it is a decision (#168 decision 3), not a defect: a
                    // per-pixel invert cannot tell a photograph from a paragraph, and the
                    // alternative that could would flatten every diagram to one colour and
                    // leave form fields drawn in daylight. Scans invert the way a night
                    // reader wants; photographs read as negatives. Better stated in the
                    // setting than discovered on a page.
                    Text("Night inverts the page, pictures included.")
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Close") { dismiss() }
                }
            }
        }
    }
}
