package com.megapdf.android

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.megapdf.engine.PageTint
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/**
 * The two reading preferences (#513), in the idiom Android has for exactly this:
 * `DataStore<Preferences>`.
 *
 * Recents ([RecentFilesStore]) and signatures ([SignatureLibraryStore]) are JSON files
 * because they are *lists of records* the app reads and writes whole. These are two scalars
 * a Settings screen binds to and the viewer observes, so a fifth JSON file would be the
 * wrong shape — the plan (`docs/reading-mode-plan.md` §2, "Storage") says so, and this is
 * the app's first `DataStore`.
 *
 * Neither preference is per document (#168 decision 2): with one setting there is nothing
 * ambiguous about which document's choice a window is showing, and no recents-store
 * migration to write.
 */
class ReadingPreferences(private val context: Context) {

    /** `Normal` is stored as the empty string, so a store never written reads back as Normal. */
    val pageTint: Flow<PageTint> =
        context.readingDataStore.data.map { PageTint.of(it[PAGE_COLOURS]) }

    /** Off by default (#168 decision 2): opening a document shows the whole app until asked otherwise. */
    val openInReadingMode: Flow<Boolean> =
        context.readingDataStore.data.map { it[OPEN_IN_READING_MODE] ?: false }

    suspend fun setPageTint(tint: PageTint) {
        context.readingDataStore.edit { it[PAGE_COLOURS] = tint.storedName }
    }

    suspend fun setOpenInReadingMode(on: Boolean) {
        context.readingDataStore.edit { it[OPEN_IN_READING_MODE] = on }
    }

    companion object {
        /**
         * The same two names the desktops' `settings.json` uses (`AppSettings.PageColours`,
         * `AppSettings.OpenInReadingMode`), so a reader of either store recognises the other.
         */
        internal val PAGE_COLOURS = stringPreferencesKey("PageColours")
        internal val OPEN_IN_READING_MODE = booleanPreferencesKey("OpenInReadingMode")
    }
}

/**
 * One store for the process, named by the delegate — `preferencesDataStore` refuses a second
 * instance over the same file, which is exactly the guard wanted here.
 */
private val Context.readingDataStore: DataStore<Preferences> by preferencesDataStore(name = "reading")
