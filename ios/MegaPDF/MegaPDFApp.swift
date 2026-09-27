import SwiftUI

@main
struct MegaPDFApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
        // #172: the iPad's keyboard commands (menu bar on iPadOS 26, the ⌘ overlay
        // before it). Nothing is declared on the iPhone, whose screen stays as it was.
        .commands {
            if UIDevice.current.userInterfaceIdiom == .pad {
                MegaPDFCommands()
            }
        }
    }
}
