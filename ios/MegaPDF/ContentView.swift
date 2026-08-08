import SwiftUI

struct ContentView: View {
    @StateObject private var model = ViewerModel()
    @State private var password = ""

    var body: some View {
        NavigationStack {
            switch model.state {
            case let .home(recents, error):
                HomeView(
                    recents: recents,
                    error: error,
                    onOpen: model.openPicked,
                    onRecent: model.openRecent
                )

            case .loading:
                ProgressView()

            case let .passwordNeeded(_, displayName, _, wrongPassword):
                VStack {
                    Text(displayName).font(.headline)
                }
                .alert("Password required", isPresented: .constant(true)) {
                    SecureField("Password", text: $password)
                    Button("Open") {
                        model.submitPassword(password)
                        password = ""
                    }
                    Button("Cancel", role: .cancel) {
                        password = ""
                        model.close()
                    }
                } message: {
                    Text(wrongPassword
                        ? "That password didn't work. Try again."
                        : "This document is protected.")
                }

            case let .viewing(displayName, pageSizes):
                ViewerView(
                    displayName: (model.isDirty ? "• " : "") + displayName,
                    pageSizes: pageSizes,
                    pageImages: model.pageImages,
                    onWindowChange: model.updateRenderWindow,
                    onPageTap: { model.onPageTapped(index: $0, xFraction: $1, yFraction: $2) },
                    onClose: model.close
                )
            }
        }
    }
}

#Preview {
    ContentView()
}
