import XCTest
@testable import MegaPDF

/// The naming rules for where a recent document lives (#165).
///
/// These run against `RecentLocation.Facts` rather than against real URLs on
/// purpose: the interesting decisions are what to name a place and what to leave
/// off, and no simulator has an iCloud account or a second file provider to make
/// those cases with. `facts(for:)` — the part that asks the file system — is
/// exercised by `testFactsForAFileInTheAppContainer` below, which is the one case
/// a test process can actually create.
final class RecentLocationTests: XCTestCase {

    private func facts(ubiquitous: Bool = false, container: String? = nil,
                       parent: String? = nil, inAppContainer: Bool = false,
                       device: String = "On My iPhone") -> RecentLocation.Facts {
        RecentLocation.Facts(isUbiquitous: ubiquitous, iCloudContainerName: container,
                             parentName: parent, isInAppContainer: inAppContainer,
                             deviceName: device)
    }

    // MARK: - what a row says

    func testICloudFileShowsItsContainerAndFolder() {
        let location = RecentLocation.from(
            facts(ubiquitous: true, container: "iCloud Drive", parent: "Clients"))
        XCTAssertEqual(location.segments, ["iCloud Drive", "Clients"])
        XCTAssertEqual(location.subtitle, "iCloud Drive › Clients")
    }

    func testICloudWithoutAContainerNameFallsBackToICloudDrive() {
        let location = RecentLocation.from(facts(ubiquitous: true, container: nil, parent: "Clients"))
        XCTAssertEqual(location.segments.first, "iCloud Drive")
    }

    func testBlankContainerNameIsNotShownAsAPlace() {
        let location = RecentLocation.from(facts(ubiquitous: true, container: "   ", parent: "Clients"))
        XCTAssertEqual(location.segments, ["iCloud Drive", "Clients"])
    }

    /// A file at the root of iCloud Drive has a parent the system also calls
    /// "iCloud Drive"; repeating it would read "iCloud Drive › iCloud Drive".
    func testAFileAtTheRootOfItsPlaceShowsThePlaceOnce() {
        let location = RecentLocation.from(
            facts(ubiquitous: true, container: "iCloud Drive", parent: "iCloud Drive"))
        XCTAssertEqual(location.segments, ["iCloud Drive"])
    }

    func testTheRootCheckIgnoresCase() {
        let location = RecentLocation.from(
            facts(ubiquitous: true, container: "iCloud Drive", parent: "iCloud drive"))
        XCTAssertEqual(location.segments, ["iCloud Drive"])
    }

    func testAFileInTheAppsOwnContainerIsOnTheDevice() {
        let location = RecentLocation.from(
            facts(parent: "Inbox", inAppContainer: true, device: "On My iPad"))
        XCTAssertEqual(location.segments, ["On My iPad", "Inbox"])
    }

    /// iOS has no public way to turn a file provider's domain identifier into
    /// "Dropbox", so a file from one is not given a place it might not have. The
    /// folder still shows, and the folder is what tells two same-named files apart.
    func testAFileFromAnUnnameableProviderShowsItsFolderAlone() {
        let location = RecentLocation.from(facts(parent: "Scans"))
        XCTAssertEqual(location.segments, ["Scans"])
    }

    func testAFileWithNothingToNameHasNoLocation() {
        XCTAssertTrue(RecentLocation.from(facts(parent: nil)).isEmpty)
    }

    // MARK: - folder names the person never chose

    func testInternalFolderNamesAreNotShown() {
        for name in ["File Provider Storage", "Mobile Documents", "com~apple~CloudDocs",
                     "Containers", "Data", "/", "  ",
                     "5C9E4A2E-4E4B-4F9A-8A1B-6E2C0A1D9F31"] {
            XCTAssertNil(RecentLocation.presentableFolderName(name),
                         "\(name) is the file system's business, not a place anyone recognises")
        }
    }

    func testOrdinaryFolderNamesAreShown() {
        for name in ["Documents", "Downloads", "Clients", "Smith", "Téléchargements",
                     "2026 Invoices", "Dossier de Hélène"] {
            XCTAssertEqual(RecentLocation.presentableFolderName(name), name)
        }
    }

    func testAFolderNameIsTrimmed() {
        XCTAssertEqual(RecentLocation.presentableFolderName("  Clients \n"), "Clients")
    }

    // MARK: - the part that reads the file system

    func testFactsForAFileInTheAppContainer() throws {
        let dir = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent("Documents", isDirectory: true)
            .appendingPathComponent("MegaPDF-165-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("agreement.pdf")
        try Data("%PDF-1.4\n".utf8).write(to: file)

        let facts = RecentLocation.facts(for: file, deviceName: "On My iPhone")
        XCTAssertTrue(facts.isInAppContainer, "a file under NSHomeDirectory is on the device")
        XCTAssertFalse(facts.isUbiquitous)
        XCTAssertEqual(facts.parentName, dir.lastPathComponent)

        // The folder here is a UUID-suffixed test directory, which is a real name,
        // so it shows; what matters is that the place was recognised.
        let location = try XCTUnwrap(RecentLocation.of(file, deviceName: "On My iPhone"))
        XCTAssertEqual(location.segments.first, "On My iPhone")
    }

    func testAMissingFileStillProducesAPlace() {
        // Nothing here throws or blocks: an entry without a location is a row
        // without a subtitle, never a failure to open a document.
        let gone = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent("Documents/Nowhere/agreement.pdf")
        let facts = RecentLocation.facts(for: gone, deviceName: "On My iPhone")
        XCTAssertTrue(facts.isInAppContainer)
        XCTAssertEqual(facts.parentName, "Nowhere")
    }

    // MARK: - what a screen reader hears

    /// Asserted by what the label contains rather than by its English wording: iOS
    /// CI runs this whole suite a second time under fr-CA, where it reads
    /// "agreement.pdf, dans iCloud Drive › Smith". What has to hold in both is that
    /// the name and the place are in there, and that a file that has gone says so.
    func testAccessibilityLabelNamesTheFileAndThePlace() {
        let entry = RecentEntry(bookmarkBase64: "Yg==", displayName: "agreement.pdf",
                                lastOpenedEpochMs: 0,
                                location: RecentLocation(segments: ["iCloud Drive", "Smith"]))
        let label = entry.accessibilityLabel()
        XCTAssertTrue(label.contains("agreement.pdf"), label)
        XCTAssertTrue(label.contains("iCloud Drive › Smith"), label)

        let gone = entry.accessibilityLabel(available: false)
        XCTAssertTrue(gone.contains("agreement.pdf"), gone)
        XCTAssertTrue(gone.contains("iCloud Drive › Smith"), gone)
        XCTAssertNotEqual(gone, label, "a file that has gone has to sound different")
    }

    func testAccessibilityLabelWithoutALocationIsJustTheName() {
        let entry = RecentEntry(bookmarkBase64: "Yg==", displayName: "agreement.pdf",
                                lastOpenedEpochMs: 0)
        // No location, no format string: the same in every language.
        XCTAssertEqual(entry.accessibilityLabel(), "agreement.pdf")

        let gone = entry.accessibilityLabel(available: false)
        XCTAssertTrue(gone.hasPrefix("agreement.pdf"), gone)
        XCTAssertNotEqual(gone, "agreement.pdf", "a file that has gone has to sound different")
    }

    /// Two rows with the same file name have to sound different, which is the whole
    /// point of #2 meeting #165.
    func testTwoSameNamedEntriesSoundDifferent() {
        let name = "agreement.pdf"
        let smith = RecentEntry(bookmarkBase64: "YQ==", displayName: name, lastOpenedEpochMs: 0,
                                location: RecentLocation(segments: ["iCloud Drive", "Smith"]))
        let jones = RecentEntry(bookmarkBase64: "Yg==", displayName: name, lastOpenedEpochMs: 0,
                                location: RecentLocation(segments: ["iCloud Drive", "Jones"]))
        XCTAssertNotEqual(smith.accessibilityLabel(), jones.accessibilityLabel())
    }
}
