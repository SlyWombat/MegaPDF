import XCTest
@testable import MegaPDF

final class RecentsStoreTests: XCTestCase {

    private var tempURL: URL!

    override func setUp() {
        tempURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("recents-\(UUID()).json")
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: tempURL)
    }

    private func store(max: Int = 10) -> RecentsStore {
        RecentsStore(fileURL: tempURL, maxEntries: max)
    }

    private func entry(_ name: String, at ms: Int64 = 0) -> RecentEntry {
        RecentEntry(
            bookmarkBase64: Data(name.utf8).base64EncodedString(),
            displayName: name,
            lastOpenedEpochMs: ms)
    }

    func testEmptyStoreLoadsEmpty() {
        XCTAssertEqual(store().load(), [])
    }

    func testAddPersistsAndSurvivesReload() {
        store().add(entry("a.pdf", at: 1))
        XCTAssertEqual(store().load().map(\.displayName), ["a.pdf"])
    }

    func testMostRecentFirstAndDeduped() {
        let s = store()
        s.add(entry("a.pdf", at: 1))
        s.add(entry("b.pdf", at: 2))
        s.add(entry("a.pdf", at: 3))
        XCTAssertEqual(s.load().map(\.displayName), ["a.pdf", "b.pdf"])
        XCTAssertEqual(s.load().first?.lastOpenedEpochMs, 3)
    }

    func testCappedAtMaxEntries() {
        let s = store(max: 3)
        for i in 1...5 { s.add(entry("\(i).pdf", at: Int64(i))) }
        XCTAssertEqual(s.load().map(\.displayName), ["5.pdf", "4.pdf", "3.pdf"])
    }

    func testRemoveDropsEntry() {
        let s = store()
        s.add(entry("a.pdf"))
        s.add(entry("b.pdf"))
        s.remove(id: entry("a.pdf").id)
        XCTAssertEqual(s.load().map(\.displayName), ["b.pdf"])
    }

    func testCorruptFileLoadsEmpty() throws {
        try Data("{ not json ]".utf8).write(to: tempURL)
        XCTAssertEqual(store().load(), [])
    }
}
