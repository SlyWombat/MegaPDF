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

    // MARK: - where each file lives (#165)

    private func entry(_ name: String, at ms: Int64 = 0, bookmark: String? = nil,
                       location: RecentLocation?) -> RecentEntry {
        RecentEntry(
            bookmarkBase64: Data((bookmark ?? name).utf8).base64EncodedString(),
            displayName: name,
            lastOpenedEpochMs: ms,
            location: location)
    }

    private func place(_ segments: String...) -> RecentLocation {
        RecentLocation(segments: segments)
    }

    /// The migration, and it really is this: an entry written before #165 has no
    /// `location` key, and a synthesised decoder reads a missing key for an optional
    /// property as nil. Nobody's list is dropped or corrupted by the new field.
    func testEntriesStoredBeforeThisFieldStillLoad() throws {
        let legacy = """
        [
          {"bookmarkBase64":"YS5wZGY=","displayName":"a.pdf","lastOpenedEpochMs":17},
          {"bookmarkBase64":"Yi5wZGY=","displayName":"b.pdf","lastOpenedEpochMs":9}
        ]
        """
        try Data(legacy.utf8).write(to: tempURL)
        let loaded = store().load()
        XCTAssertEqual(loaded.map(\.displayName), ["a.pdf", "b.pdf"])
        XCTAssertEqual(loaded.map(\.lastOpenedEpochMs), [17, 9])
        XCTAssertNil(loaded[0].location)
        XCTAssertNil(loaded[1].location)
    }

    func testLocationSurvivesARoundTrip() {
        let s = store()
        s.add(entry("a.pdf", location: place("iCloud Drive", "Smith")))
        XCTAssertEqual(s.load().first?.location, place("iCloud Drive", "Smith"))
    }

    /// The point of the whole issue: two files called the same thing, in different
    /// folders, are two rows. Before #165 the second one silently replaced the first,
    /// because entries were deduped by display name.
    func testSameNameFromTwoFoldersKeepsBothRows() {
        let s = store()
        s.add(entry("agreement.pdf", at: 1, bookmark: "smith",
                    location: place("iCloud Drive", "Smith")))
        s.add(entry("agreement.pdf", at: 2, bookmark: "jones",
                    location: place("iCloud Drive", "Jones")))
        XCTAssertEqual(s.load().count, 2)
        XCTAssertEqual(s.load().compactMap { $0.location?.subtitle },
                       ["iCloud Drive › Jones", "iCloud Drive › Smith"])
    }

    /// Re-opening one file does not add a second row for it, even though the
    /// bookmark bytes a fresh open produces are not the ones stored last time.
    func testReopeningTheSameFileUpdatesItsRow() {
        let s = store()
        s.add(entry("agreement.pdf", at: 1, bookmark: "first-bookmark",
                    location: place("iCloud Drive", "Smith")))
        s.add(entry("agreement.pdf", at: 2, bookmark: "second-bookmark",
                    location: place("iCloud Drive", "Smith")))
        XCTAssertEqual(s.load().count, 1)
        XCTAssertEqual(s.load().first?.lastOpenedEpochMs, 2)
    }

    /// An entry from before #165 has nothing to compare but its name, so its own
    /// newer entry collapses into it rather than sitting beside it as a twin.
    func testAnEntryWithNoLocationCollapsesIntoItsNewerSelf() {
        let s = store()
        s.add(entry("agreement.pdf", at: 1, bookmark: "old", location: nil))
        s.add(entry("agreement.pdf", at: 2, bookmark: "new",
                    location: place("iCloud Drive", "Smith")))
        XCTAssertEqual(s.load().count, 1)
        XCTAssertEqual(s.load().first?.location, place("iCloud Drive", "Smith"))
    }

    func testSetLocationFillsInAnOlderEntry() {
        let s = store()
        s.add(entry("a.pdf", location: nil))
        let id = s.load()[0].id
        s.setLocation(place("On My iPhone", "Downloads"), id: id)
        XCTAssertEqual(s.load().first?.location, place("On My iPhone", "Downloads"))
    }

    /// A backfill that finishes after somebody has removed the row must not put it
    /// back.
    func testSetLocationOnARemovedEntryDoesNothing() {
        let s = store()
        s.add(entry("a.pdf", location: nil))
        let id = s.load()[0].id
        s.remove(id: id)
        s.setLocation(place("On My iPhone", "Downloads"), id: id)
        XCTAssertEqual(s.load(), [])
    }
}
