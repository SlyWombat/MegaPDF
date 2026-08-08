import SwiftUI

// Scaffold stage (#20): proves the Mac-less CI pipeline builds a SwiftUI app.
// The real viewer starts after the engine ADR (docs/adr-001-ios-pdf-engine.md)
// is decided.
struct ContentView: View {
    var body: some View {
        VStack(spacing: 8) {
            Text("MegaPDF")
                .font(.largeTitle.bold())
            Text("Open. Fix. Save. Done.")
                .foregroundStyle(.secondary)
        }
        .padding()
    }
}

#Preview {
    ContentView()
}
