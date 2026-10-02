import XCTest
@testable import MegaPDF

/// Waiting for a `ViewerModel` to finish opening, by the model's own account (#599).
///
/// Nine files had written the same fifteen-second poll and the same five-line condition, and
/// `DynamicXfaViewerModelTests` is where it finally went red — "timed out waiting for: the
/// document opens", on a hosted runner, in a *unit* test with no XCUITest anywhere near it.
/// That is the same sentence `PageToolsUITests` produces at the XCUITest layer and that
/// `BodyTextEditUITests` produces at the launch itself: three layers, one condition, which is
/// a machine on which opening a document takes longer than whatever number was written down.
/// The same test measured 14 s on our Mac, 98 s and 105 s on two hosted runners, and 530 s —
/// passing — on a third, so there is no number to pick (#515).
///
/// So the deadline is renewed for as long as the model is **still blocked**, which is the
/// model's own way of saying it is working, up to a cap. A model that wedges without being
/// blocked still fails in `grace`.
///
/// `DocumentOpening` in the UI test target is the same idea against `busyOpening`.
enum ModelOpening {

    /// The condition all nine copies had: viewing, with a document, and nothing blocking.
    @MainActor
    static func opened(_ model: ViewerModel) -> Bool {
        if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
        return false
    }

    /// Waits for `condition`, renewing the deadline while the model reports itself blocked.
    @MainActor
    static func wait(_ what: String, _ model: ViewerModel, grace: TimeInterval = 15,
                     cap: TimeInterval = 240, file: StaticString = #filePath, line: UInt = #line,
                     until condition: () -> Bool) async throws {
        let start = Date()
        var deadline = start.addingTimeInterval(grace)
        while !condition() {
            if model.busy.isBlocked { deadline = Date().addingTimeInterval(grace) }
            if Date() > deadline || Date().timeIntervalSince(start) > cap {
                XCTFail("timed out waiting for: \(what)"
                        + (model.busy.isBlocked ? " — the model is still blocked" : ""),
                        file: file, line: line)
                throw CancellationError()
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    /// The nine-times-repeated wait, once.
    @MainActor
    static func waitForTheDocument(_ model: ViewerModel, file: StaticString = #filePath,
                                   line: UInt = #line) async throws {
        try await wait("the document opens", model, file: file, line: line) { opened(model) }
    }
}
