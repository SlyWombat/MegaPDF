import Foundation
#if canImport(UIKit)
import UIKit
#endif

/// Where a recent document lives, as the Files app would say it (#165).
///
/// A recent list shows file names, and files from one template or one scanner
/// share them: three rows reading "agreement.pdf" tell nobody which is which.
/// What separates them is the folder, so every row carries its location — not
/// only the rows whose names clash, which would make the list jump about as
/// entries come and go.
///
/// Recorded when the file is opened, not derived when the list is drawn. A
/// `RecentEntry` holds a security-scoped bookmark and nothing else; resolving ten
/// bookmarks and taking security-scoped access on each of them to label a list is
/// the wrong thing to do while a view is being laid out. At the moment a document
/// is opened the app has the URL, and the right to read around it, which is
/// exactly what this needs.
///
/// iOS shows no paths, so neither does this: display names only, at most two of
/// them, in the app's language.
struct RecentLocation: Codable, Equatable, Hashable, Sendable {
    /// Outermost first, at most two: `["iCloud Drive", "Clients"]`, `["Downloads"]`.
    /// Never a path, a container identifier or a `file://` URL.
    let segments: [String]

    /// The line under the file name. Finder's path bar and the Files app both
    /// separate places with "›", and the Windows and Mac halves of #165 use it too.
    var subtitle: String { segments.joined(separator: " › ") }

    var isEmpty: Bool { segments.isEmpty }
}

extension RecentLocation {

    // MARK: - the rules

    /// What the file system said about a file. Everything the naming rules are
    /// allowed to use, gathered in one place so that the rules can be tested
    /// without an iCloud account, a file provider or a device.
    struct Facts: Equatable, Sendable {
        /// `URLResourceKey.isUbiquitousItemKey`: the item is in iCloud.
        var isUbiquitous = false
        /// `ubiquitousItemContainerDisplayNameKey`, when iCloud gave one.
        var iCloudContainerName: String?
        /// The parent folder's `localizedName`, else its last path component.
        var parentName: String?
        /// The file is inside this app's own container, so it is on the device.
        var isInAppContainer = false
        /// "On My iPhone" or "On My iPad", localised.
        var deviceName: String = ""

        init(isUbiquitous: Bool = false, iCloudContainerName: String? = nil,
             parentName: String? = nil, isInAppContainer: Bool = false,
             deviceName: String = "") {
            self.isUbiquitous = isUbiquitous
            self.iCloudContainerName = iCloudContainerName
            self.parentName = parentName
            self.isInAppContainer = isInAppContainer
            self.deviceName = deviceName
        }
    }

    /// Folder names that are the file system's business rather than a person's.
    /// A file provider keeps its files under "File Provider Storage" inside an app
    /// group whose folder is a UUID, and iCloud's own folder is
    /// "com~apple~CloudDocs". None of those is a place anybody would recognise,
    /// and the spec for this is explicit that a row never shows container paths.
    private static let internalFolderNames: Set<String> = [
        "File Provider Storage", "Mobile Documents", "Containers", "AppGroup", "Data",
    ]

    /// The folder name to show, or nil when the name is the file system's and not
    /// the person's. A folder called "Documents" or "Downloads" is a real place and
    /// stays; one called "com~apple~CloudDocs" or a bare UUID does not.
    static func presentableFolderName(_ raw: String?) -> String? {
        guard let trimmed = raw?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty,
              trimmed != "/",
              !trimmed.contains("~"),
              !trimmed.contains("/"),
              UUID(uuidString: trimmed) == nil,
              !internalFolderNames.contains(trimmed)
        else { return nil }
        return trimmed
    }

    /// The location a row shows, from what the file system said.
    ///
    /// The place comes first when there is one to name: iCloud says so itself, and
    /// a file inside this app's own container is on the device. A file reached
    /// through some other provider cannot be named — iOS offers no public way to
    /// turn a file provider's domain identifier into "Dropbox" — so rather than
    /// guess, such a row shows its folder alone, which is the part that tells two
    /// same-named files apart anyway.
    static func from(_ facts: Facts) -> RecentLocation {
        var place: String?
        if facts.isUbiquitous {
            let named = facts.iCloudContainerName?.trimmingCharacters(in: .whitespacesAndNewlines)
            place = (named?.isEmpty == false ? named : nil) ?? String(localized: "iCloud Drive")
        } else if facts.isInAppContainer {
            place = facts.deviceName.isEmpty ? nil : facts.deviceName
        }

        var segments: [String] = []
        if let place { segments.append(place) }
        // A file at the root of its place has a parent the system names after the
        // place itself ("iCloud Drive"); it adds nothing, so it is left off.
        if let folder = presentableFolderName(facts.parentName),
           folder.caseInsensitiveCompare(place ?? "") != .orderedSame {
            segments.append(folder)
        }
        return RecentLocation(segments: segments)
    }

    // MARK: - reading the facts

    /// "On My iPhone" or "On My iPad", the way the Files app labels the device.
    static func deviceName() -> String {
        #if canImport(UIKit)
        if UIDevice.current.userInterfaceIdiom == .pad {
            return String(localized: "On My iPad",
                          comment: "#165: where a recent file lives; the Files app's name for this iPad")
        }
        #endif
        return String(localized: "On My iPhone",
                      comment: "#165: where a recent file lives; the Files app's name for this iPhone")
    }

    /// Reads the facts for a file that is open.
    ///
    /// The caller must already hold security-scoped access — `ViewerModel` holds it
    /// for the document's whole life — because the parent folder's display name is
    /// outside the sandbox extension the picker grants otherwise. Every read is
    /// best-effort: a folder name that cannot be read falls back to the last path
    /// component, and a row with no location at all is better than a wrong one.
    ///
    /// `deviceName` is passed in rather than read here: this runs off the main actor,
    /// away from the view being drawn, and `UIDevice` belongs on the main one.
    static func facts(for url: URL, deviceName: String) -> Facts {
        var facts = Facts()
        facts.deviceName = deviceName
        facts.isInAppContainer = url.standardizedFileURL.path
            .hasPrefix(URL(fileURLWithPath: NSHomeDirectory()).standardizedFileURL.path)

        let values = try? url.resourceValues(
            forKeys: [.isUbiquitousItemKey, .ubiquitousItemContainerDisplayNameKey])
        facts.isUbiquitous = values?.isUbiquitousItem ?? false
        facts.iCloudContainerName = values?.ubiquitousItemContainerDisplayName

        let parent = url.deletingLastPathComponent()
        facts.parentName =
            (try? parent.resourceValues(forKeys: [.localizedNameKey]))?.localizedName
            ?? parent.lastPathComponent.removingPercentEncoding
            ?? parent.lastPathComponent
        return facts
    }

    /// The location to store for a file being opened, or nil when nothing about it
    /// can be named. Never throws: a recent entry without a location is a row
    /// without a subtitle, not a failure to open a document.
    static func of(_ url: URL, deviceName: String) -> RecentLocation? {
        let location = from(facts(for: url, deviceName: deviceName))
        return location.isEmpty ? nil : location
    }
}
