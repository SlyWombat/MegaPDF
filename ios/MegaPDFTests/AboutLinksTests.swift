import XCTest
@testable import MegaPDF

/// Where About sends people (#730): the support page on the site's real home, and the
/// source code. The addresses are the ones every store listing and the website use.
final class AboutLinksTests: XCTestCase {
    func testSupportIsTheSitesSupportPage() {
        XCTAssertEqual(AboutLinks.support.absoluteString, "https://megapdf.ca/support/")
    }

    func testSourceIsTheGitHubRepository() {
        XCTAssertEqual(AboutLinks.source.absoluteString, "https://github.com/SlyWombat/MegaPDF")
    }
}
