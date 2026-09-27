import SwiftUI

/// Hardware-keyboard commands for the iPad (#172).
///
/// Declared on the scene rather than as `.keyboardShortcut` on the buttons, because that
/// is what the platform builds its own furniture from: the ⌘-hold shortcut overlay on every
/// iPadOS since 14, and the menu bar iPadOS 26 draws at the top of the screen. A shortcut
/// on a button fires; a command is also listed.
///
/// What a command acts on is whatever view is on screen — Home publishes Open, the viewer
/// publishes the rest — through the scene's focused values, so this file knows nothing
/// about the model. A closure that is `nil` is a command that cannot be done right now,
/// and the item is disabled rather than absent, as a menu bar expects.
///
/// iPad only: the iPhone has no overlay and no menu bar, and its screen is left exactly
/// as it was.
struct ViewerCommandTarget {
    var save: (() -> Void)?
    var close: (() -> Void)?
    var find: (() -> Void)?
    var undo: (() -> Void)?
    var redo: (() -> Void)?
}

struct OpenPDFCommandTarget {
    let open: () -> Void
}

private struct ViewerCommandTargetKey: FocusedValueKey {
    typealias Value = ViewerCommandTarget
}

private struct OpenPDFCommandTargetKey: FocusedValueKey {
    typealias Value = OpenPDFCommandTarget
}

extension FocusedValues {
    var viewerCommands: ViewerCommandTarget? {
        get { self[ViewerCommandTargetKey.self] }
        set { self[ViewerCommandTargetKey.self] = newValue }
    }

    var openPDFCommand: OpenPDFCommandTarget? {
        get { self[OpenPDFCommandTargetKey.self] }
        set { self[OpenPDFCommandTargetKey.self] = newValue }
    }
}

struct MegaPDFCommands: Commands {
    @FocusedValue(\.viewerCommands) private var viewer
    @FocusedValue(\.openPDFCommand) private var home

    var body: some Commands {
        // The Home screen's Open PDF, and Close for an open document. Close is ⌘W, the
        // key every Apple platform closes a document window with; the app has one
        // document open at a time, so closing it is what the key means here.
        CommandGroup(after: .newItem) {
            Button("Open PDF…") { home?.open() }
                .keyboardShortcut("o", modifiers: .command)
                .disabled(home == nil)
            Button("Close") { viewer?.close?() }
                .keyboardShortcut("w", modifiers: .command)
                .disabled(viewer?.close == nil)
        }
        CommandGroup(replacing: .saveItem) {
            Button("Save") { viewer?.save?() }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(viewer?.save == nil)
        }
        // The document's own history, not the text field's: a focused field keeps its
        // ⌘Z for itself further up the responder chain, and these only reach the page.
        CommandGroup(replacing: .undoRedo) {
            Button("Undo") { viewer?.undo?() }
                .keyboardShortcut("z", modifiers: .command)
                .disabled(viewer?.undo == nil)
            Button("Redo") { viewer?.redo?() }
                .keyboardShortcut("z", modifiers: [.command, .shift])
                .disabled(viewer?.redo == nil)
        }
        CommandGroup(after: .textEditing) {
            Button("Find in document") { viewer?.find?() }
                .keyboardShortcut("f", modifiers: .command)
                .disabled(viewer?.find == nil)
        }
    }
}
