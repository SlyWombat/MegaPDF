import SwiftUI

/// Scrolling page list with pinch/double-tap zoom, tap-to-edit dispatch,
/// signature placement chrome, and save/sign toolbar.
struct ViewerView: View {
    @ObservedObject var model: ViewerModel
    let displayName: String
    let pageSizes: [CGSize]
    let onSaveCopy: () -> Void
    let onClose: () -> Void

    @State private var zoom: CGFloat = 1
    @State private var gestureZoom: CGFloat = 1
    @State private var visible: Set<Int> = []
    @State private var signaturesOpen = false
    @State private var confirmDiscard = false
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
        }
        .navigationTitle((model.isDirty ? "• " : "") + displayName)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) {
                Button("Close") {
                    if model.isDirty { confirmDiscard = true } else { onClose() }
                }
            }
            ToolbarItemGroup(placement: .navigationBarTrailing) {
                Button("Sign") { signaturesOpen = true }
                Button(model.isSaving ? "Saving…" : "Save") { model.save() }
                    .disabled(!model.isDirty || model.isSaving)
                Menu {
                    Button("Save a copy", action: onSaveCopy)
                        .disabled(model.isSaving)
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
        .sheet(isPresented: $signaturesOpen) {
            SignaturesSheet(
                signatures: model.signatures,
                onPick: { entry in
                    signaturesOpen = false
                    model.startPlacement(entry)
                },
                onDrawn: model.addDrawnSignature,
                onPhoto: model.importSignature,
                onDelete: model.deleteSignature,
                onDismiss: { signaturesOpen = false }
            )
        }
        .alert("Unsaved changes", isPresented: $confirmDiscard) {
            Button("Save") { model.save() }
            Button("Discard", role: .destructive, action: onClose)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This document has unsaved changes.")
        }
    }

    @ViewBuilder
    private func pageView(index: Int, containerWidth: CGFloat) -> some View {
        let size = pageSizes[index]
        let width = containerWidth * effectiveZoom
        let height = width * size.height / size.width
        ZStack(alignment: .topLeading) {
            if let image = model.pageImages[index] {
                Image(uiImage: UIImage(cgImage: image))
                    .resizable()
                    .interpolation(.high)
            } else {
                Color.white  // placeholder keeps layout stable until the render lands
            }
            if let stamp = model.selectedStamp, stamp.pageIndex == index {
                StampOverlay(
                    stamp: stamp,
                    pageSize: size,
                    viewSize: CGSize(width: width, height: height),
                    onCommit: model.commitStampRect,
                    onRemove: model.removeSelectedStamp
                )
            }
        }
        .frame(width: width, height: height)
        .clipped()
        // Double-tap zoom is checked first; a lone tap (deferred briefly by
        // the exclusivity) dispatches to the model — as on Android.
        .gesture(
            SpatialTapGesture(count: 2)
                .onEnded { _ in
                    zoom = zoom < 1.5 ? 2 : 1
                    pushWindow(containerWidth: containerWidth)
                }
                .exclusively(before: SpatialTapGesture()
                    .onEnded { value in
                        model.onPageTapped(
                            index: index,
                            xFraction: Double(value.location.x / width),
                            yFraction: Double(value.location.y / height))
                    })
        )
        .accessibilityLabel("Page \(index + 1)")
    }

    private func pushWindow(containerWidth: CGFloat) {
        guard let first = visible.min(), let last = visible.max() else { return }
        let widthPx = Int(containerWidth * effectiveZoom * displayScale)
        model.updateRenderWindow(first: first, last: last, widthPx: widthPx)
    }
}
