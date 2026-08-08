import SwiftUI

/// Scrolling page list with pinch/double-tap zoom and visible-range tracking
/// feeding the model's ±2-page render window.
struct ViewerView: View {
    let displayName: String
    let pageSizes: [CGSize]
    let pageImages: [Int: CGImage]
    let onWindowChange: (_ first: Int, _ last: Int, _ widthPx: Int) -> Void
    let onClose: () -> Void

    @State private var zoom: CGFloat = 1
    @State private var gestureZoom: CGFloat = 1
    @State private var visible: Set<Int> = []
    @Environment(\.displayScale) private var displayScale

    private var effectiveZoom: CGFloat { min(max(zoom * gestureZoom, 1), 4) }

    var body: some View {
        GeometryReader { geo in
            ScrollView(effectiveZoom > 1 ? [.vertical, .horizontal] : .vertical) {
                LazyVStack(spacing: 8) {
                    ForEach(pageSizes.indices, id: \.self) { index in
                        pageView(index: index, containerWidth: geo.size.width)
                            .onAppear {
                                visible.insert(index)
                                pushWindow(containerWidth: geo.size.width)
                            }
                            .onDisappear {
                                visible.remove(index)
                                pushWindow(containerWidth: geo.size.width)
                            }
                    }
                }
                .frame(minWidth: geo.size.width)
            }
            .background(Color(white: 0.25))
            .gesture(
                MagnificationGesture()
                    .onChanged { gestureZoom = $0 }
                    .onEnded { value in
                        zoom = min(max(zoom * value, 1), 4)
                        gestureZoom = 1
                        pushWindow(containerWidth: geo.size.width)
                    }
            )
            .onTapGesture(count: 2) {
                zoom = zoom < 1.5 ? 2 : 1
                pushWindow(containerWidth: geo.size.width)
            }
        }
        .navigationTitle(displayName)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) {
                Button("Close", action: onClose)
            }
        }
    }

    @ViewBuilder
    private func pageView(index: Int, containerWidth: CGFloat) -> some View {
        let size = pageSizes[index]
        let width = containerWidth * effectiveZoom
        let height = width * size.height / size.width
        Group {
            if let image = pageImages[index] {
                Image(uiImage: UIImage(cgImage: image))
                    .resizable()
                    .interpolation(.high)
            } else {
                Color.white  // placeholder keeps layout stable until the render lands
            }
        }
        .frame(width: width, height: height)
        .accessibilityLabel("Page \(index + 1)")
    }

    private func pushWindow(containerWidth: CGFloat) {
        guard let first = visible.min(), let last = visible.max() else { return }
        let widthPx = Int(containerWidth * effectiveZoom * displayScale)
        onWindowChange(first, last, widthPx)
    }
}
