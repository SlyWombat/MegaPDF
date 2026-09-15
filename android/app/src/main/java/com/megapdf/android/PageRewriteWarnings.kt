package com.megapdf.android

/**
 * The once-per-page warning before a change the layout guard never judges (#139). Text boxes
 * make PDFium regenerate their page's content just as a body-text edit does, and on a page its
 * writer cannot write back faithfully that changes parts of the page the person never touched.
 * Those changes are never refused: the view model asks the engine before the first one on a
 * page and warns with Continue and Cancel. One instance per viewer; [reset] whenever another
 * document is opened, so a page is asked about at most once per open document.
 */
class PageRewriteWarnings {
    private val settled = mutableSetOf<Int>()

    /** True once the page passed the check or the person chose Continue for it. */
    @Synchronized
    fun isSettled(pageIndex: Int): Boolean = pageIndex in settled

    @Synchronized
    fun settle(pageIndex: Int) {
        settled += pageIndex
    }

    @Synchronized
    fun reset() {
        settled.clear()
    }

    companion object {
        /**
         * Whether [operation] regenerates its page's content without the text guard judging it.
         * Body-text edits and deletes are judged (and refused) on their own; signatures, check
         * marks and form values are annotations and leave the content alone.
         */
        fun regeneratesUnjudged(operation: PdfEditOperation): Boolean =
            operation is TextBoxOperation || operation is EditTextBoxOperation || operation is MoveTextBoxOperation
    }
}
