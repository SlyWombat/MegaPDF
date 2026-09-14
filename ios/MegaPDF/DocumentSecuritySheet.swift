import SwiftUI

/// What the Password command opens as (#131, ADR-004 §3 and §5). Chosen when the sheet
/// opens, so its content doesn't flip underneath its own dismissal once an unlock has
/// given the document full access.
enum SecuritySheetMode: Identifiable {
    /// A restricted open: explain, and take the owner password.
    case unlock
    /// No security yet: set a password.
    case set
    /// Protected, with full access: change the password or remove it.
    case change

    var id: Self { self }
}

/// Unlocking a restricted document, and setting, changing or removing its password
/// (#131). A sheet, not an alert: the new password is typed twice with inline
/// validation, and a wrong owner password is reported without closing it.
///
/// What is typed lives in this view's own state, is cleared as soon as it is handed
/// over, and goes with the sheet. The app never stores or logs it.
struct DocumentSecuritySheet: View {
    let mode: SecuritySheetMode
    /// The document has unsaved changes; a new or removed password saves them too.
    let savesChanges: Bool
    let isBusy: Bool
    /// Set by the model when an unlock fails; shown under the field.
    let error: String?
    let onUnlock: (String) -> Void
    let onSetPassword: (String) -> Void
    let onRemovePassword: () -> Void
    let onCancel: () -> Void

    private enum Action: Hashable { case change, remove }
    private enum Step { case unlock, newPassword, remove }

    @State private var action: Action = .change
    @State private var entry = ""
    @State private var confirmation = ""
    @FocusState private var focused: Bool

    private var step: Step {
        switch mode {
        case .unlock: return .unlock
        case .set: return .newPassword
        case .change: return action == .change ? .newPassword : .remove
        }
    }

    private var mismatch: Bool { !confirmation.isEmpty && entry != confirmation }

    private var canCommit: Bool {
        guard !isBusy else { return false }
        switch step {
        case .unlock: return !entry.isEmpty
        case .newPassword: return !entry.isEmpty && entry == confirmation
        case .remove: return true
        }
    }

    // Typed as keys so every branch is looked up in the catalog.
    private var title: LocalizedStringKey {
        switch mode {
        case .unlock: return "Unlock document"
        case .set: return "Set password"
        case .change: return "Password"
        }
    }

    private var commitLabel: LocalizedStringKey {
        switch step {
        case .unlock: return "Unlock"
        case .newPassword: return mode == .set ? "Set" : "Change"
        case .remove: return "Remove"
        }
    }

    var body: some View {
        NavigationStack {
            Form {
                switch mode {
                case .unlock:
                    unlockSection
                case .set:
                    newPasswordSection
                case .change:
                    Section {
                        Picker("Password", selection: $action) {
                            Text("Change").tag(Action.change)
                            Text("Remove").tag(Action.remove)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                    }
                    if action == .change {
                        newPasswordSection
                    } else {
                        removeSection
                    }
                }
            }
            .onChange(of: action) { _ in clear() }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        clear()
                        onCancel()
                    }
                    .disabled(isBusy)
                }
                ToolbarItem(placement: .confirmationAction) {
                    if isBusy {
                        ProgressView()
                    } else {
                        Button(commitLabel, action: commit)
                            .disabled(!canCommit)
                            .accessibilityIdentifier("securityCommit")
                    }
                }
            }
            .onAppear { focused = mode != .change }
        }
        .presentationDetents([.medium, .large])
        .interactiveDismissDisabled(isBusy)
    }

    private var unlockSection: some View {
        Section {
            SecureField("Owner password", text: $entry)
                .focused($focused)
                .submitLabel(.go)
                .onSubmit(commit)
                .accessibilityIdentifier("ownerPasswordField")
        } footer: {
            VStack(alignment: .leading, spacing: 6) {
                if let error {
                    Text(error).foregroundColor(.red)
                }
                Text("The owner of this document restricted what can be changed in it. Enter the owner password to unlock it.")
            }
        }
    }

    private var newPasswordSection: some View {
        Section {
            SecureField("Password", text: $entry)
                .focused($focused)
                .submitLabel(.next)
                .accessibilityIdentifier("newPasswordField")
            SecureField("Confirm password", text: $confirmation)
                .submitLabel(.done)
                .onSubmit(commit)
                .accessibilityIdentifier("confirmPasswordField")
        } footer: {
            VStack(alignment: .leading, spacing: 6) {
                if mismatch {
                    Text("The passwords don't match.").foregroundColor(.red)
                }
                Text("Anyone opening this document will need this password. MegaPDF can't recover it if it's forgotten.")
                if savesChanges {
                    Text("This also saves your changes.")
                }
            }
        }
    }

    private var removeSection: some View {
        Section {
            Text("Anyone will be able to open this document without a password.")
        } footer: {
            if savesChanges {
                Text("This also saves your changes.")
            }
        }
    }

    private func commit() {
        guard canCommit else { return }
        switch step {
        case .unlock:
            // Cleared now: a wrong password is retyped, not edited.
            let typed = entry
            clear()
            onUnlock(typed)
        case .newPassword:
            let typed = entry
            clear()
            onSetPassword(typed)
        case .remove:
            onRemovePassword()
        }
    }

    private func clear() {
        entry = ""
        confirmation = ""
    }
}
