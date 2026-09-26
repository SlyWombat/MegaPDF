package com.megapdf.android

import android.content.Intent

/**
 * The document [MainActivity] is asked to open when another app hands it an intent (#376) —
 * Outlook, Gmail, the Files app, a browser download — rather than through this app's own
 * "Open" picker. Kept generic and free of `android.net.Uri`/`Intent` method calls so it can
 * be unit tested without Robolectric, which this module's test classpath does not have.
 */
object ViewIntent {
    /** [data] when [action] is `ACTION_VIEW`, so there is something to open; null otherwise. */
    fun <T> uriToOpen(action: String?, data: T?): T? =
        if (action == Intent.ACTION_VIEW) data else null
}
