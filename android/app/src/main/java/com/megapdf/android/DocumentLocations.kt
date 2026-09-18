package com.megapdf.android

import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.storage.StorageManager
import android.provider.DocumentsContract

/**
 * Asks Android where one of its documents lives (#165).
 *
 * Called once, when a document is opened — never while the recents list is drawn.
 * Whatever is learned is recorded with the recent entry, and [RecentLocation]
 * turns it into a line.
 *
 * ## What a normal app is allowed to know
 *
 * The obvious route is `DocumentsContract.buildRootsUri`, which is what the file
 * picker itself uses to get "Downloads", "Drive", the SD card's name. **An app
 * cannot read it.** Measured on API 36:
 *
 * ```
 * SecurityException: Permission Denial: reading
 *   com.android.providers.downloads.DownloadStorageProvider uri
 *   content://com.android.providers.downloads.documents/root
 *   requires that you obtain access using ACTION_OPEN_DOCUMENT or related APIs
 * ```
 *
 * A root listing is guarded by `MANAGE_DOCUMENTS`, which only the system picker
 * holds. A document grant buys the document, not the catalogue it came from.
 *
 * So the root name comes from two places that need no permission at all, and both
 * are names Android itself shows people:
 *
 * - **External storage** — `StorageManager` names every volume, and
 *   `StorageVolume.getDescription` is the localised string the system uses for it.
 *   The volume's uuid is also what external storage puts in front of the colon in
 *   its document ids, so the two line up exactly.
 * - **Every other provider** — the provider's own app label. That is what the
 *   picker's drawer shows beside each entry, and what a person would call it:
 *   "Drive", "Downloads", "Dropbox".
 *
 * Neither is the root *title* the picker draws, and on a rare provider the two can
 * differ. Showing the app a document came from is still the truthful answer to
 * "where does this live", and it is the one #165 rules out getting wrong: no
 * `content://`, no `/storage/emulated/0/…`, no document ids.
 */
object DocumentLocations {

    /**
     * The Downloads provider, whose app label is not its name.
     *
     * The picker's drawer calls this root **Downloads**; the app that owns it is
     * called "Local storage" on some builds and "Downloads" on others, so the
     * label is a coin toss. This is the one root common enough, and stable enough,
     * to be worth naming ourselves — and the folder a person is most likely to
     * have opened a PDF from.
     */
    private const val DOWNLOADS = "com.android.providers.downloads.documents"

    /**
     * The media provider, which is the device's own storage under another name.
     *
     * The picker reaches the same files two ways — by folder through external
     * storage, and by kind ("Documents", "Images") through this one — so the same
     * file can arrive with either authority. Its app label is "Local storage" on
     * this image, which is nobody's name for anywhere, and it made one file show as
     * "Downloads" in one row and "Local storage" in another. Its ids are opaque
     * (`document:38`), so there are no folders to add; the truthful answer is the
     * volume it is on, which is the same words external storage would have given
     * for that file (#165).
     */
    private const val MEDIA = "com.android.providers.media.documents"

    /** Where [uri] lives, outermost first, or empty if Android will not say. */
    fun segmentsFor(context: Context, uri: Uri): List<String> {
        val authority = uri.authority
        val documentId = try {
            DocumentsContract.getDocumentId(uri)
        } catch (_: Exception) {
            null   // not a document URI at all — the demo, or a plain file
        }
        return RecentLocation.segments(authority, documentId, rootName(context, authority, documentId))
    }

    private fun rootName(context: Context, authority: String?, documentId: String?): String? =
        when (authority) {
            null -> null
            RecentLocation.EXTERNAL_STORAGE ->
                volumeDescription(context, documentId?.substringBefore(':', ""))
                    ?: providerLabel(context, authority)
            DOWNLOADS -> context.getString(R.string.location_downloads)
            MEDIA -> volumeDescription(context, "primary") ?: providerLabel(context, authority)
            else -> providerLabel(context, authority)
        }

    /**
     * What the system calls the volume whose external-storage root id is [rootId].
     *
     * "primary" is the built-in storage; anything else is a volume uuid, which is
     * what a removable card reports.
     */
    private fun volumeDescription(context: Context, rootId: String?): String? {
        if (rootId.isNullOrEmpty()) return null
        return try {
            val storage = context.getSystemService(StorageManager::class.java) ?: return null
            storage.storageVolumes.firstOrNull { volume ->
                if (rootId.equals("primary", ignoreCase = true)) volume.isPrimary
                else rootId.equals(volume.uuid, ignoreCase = true)
            }?.getDescription(context)
        } catch (_: Exception) {
            null
        }
    }

    /** The name of the app that owns [authority] — "Drive", "Downloads", "Dropbox". */
    private fun providerLabel(context: Context, authority: String): String? = try {
        val packages = context.packageManager
        packages.resolveContentProvider(authority, 0)
            ?.loadLabel(packages)
            ?.toString()
            ?.trim()
            ?.takeIf { it.isNotEmpty() }
    } catch (_: Exception) {
        null
    }
}
