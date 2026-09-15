package com.megapdf.android

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * Whether the open document has unsaved changes, and D3 of #145: a save marks the document
 * saved only when nothing changed while it ran. A change that lands during a save may or may not
 * be in the written file, so the document stays dirty and a later close still asks.
 */
class DirtyTracker {
    private var edits = 0L

    var isDirty: Boolean by mutableStateOf(false)
        private set

    fun markEdited() {
        edits++
        isDirty = true
    }

    /** Taken before a save starts; hand it to [markSaved] once the file is written. */
    fun beginSave(): Long = edits

    /** Marks the document saved and returns true, unless something changed since [mark]. */
    fun markSaved(mark: Long): Boolean {
        if (edits != mark) return false
        isDirty = false
        return true
    }

    /** Another document: clean, and a save still running for the last one can't mark it saved. */
    fun reset() {
        edits++
        isDirty = false
    }
}
