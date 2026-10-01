package com.megapdf.android

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CompletableDeferred

/**
 * The kinds of thing a document's author can ask MegaPDF not to do, and the grain at which the
 * person is asked about it (#558, ADR-004 decision 11).
 *
 * **A class is a permission bit, not a command and not a document.** That is the whole of the
 * rule, and it is the bit rather than the command because the bit is what the author actually
 * set: it is the only thing the prompt can honestly name, and it is the only grain at which the
 * answer transfers. Someone who has decided to go on editing a restricted document has decided
 * about *editing* — not about one keystroke, which is why a prompt per change would be nagging
 * rather than consent, and not about rearranging its pages, which the author said separately and
 * may well have meant differently.
 *
 * Three classes, because three bits are what MegaPDF reads. [EDITING] and [ASSEMBLY] are the two
 * #558's decision names. [EXTRACTION] is here because the copy bit is *already* read — the core
 * has required it of `megapdf_pages_extract` since #174 and Android has gated Save selection as
 * on it since — so leaving it out would not have left it unconsulted, it would have left one
 * wall standing in the middle of the new rule.
 *
 * Deliberately absent:
 * - **Print** (P-bit 3) — MegaPDF's print goes out to the OS, which asks the document again.
 * - **Accessibility** (P-bit 10) — no operation here can honestly claim to be assistive
 *   technology, so there is no correct place to grant its narrower exception.
 * - **Full access** — setting, changing or removing the password is not advisory. Without the
 *   owner password there is no credential to write a new copy with, so there is nothing to
 *   override and nothing to offer; that one stays a refusal, and says so.
 * - **Another document's copy bit**, when its pages are imported here. The choice is a statement
 *   about the document the person opened and may well be the author of; a second file handed to
 *   it carries no such statement.
 */
enum class PermissionClass { EDITING, ASSEMBLY, EXTRACTION }

/**
 * What the person has chosen to go on past, for as long as this document is open (#558).
 *
 * Shaped on [PageRewriteQuestion], which is the same problem already solved once: a change that
 * needs a yes puts a question up, waits for it, and remembers the answer so it is never asked
 * twice about the same thing. The remembering is in memory and dies with the document — a choice
 * about this document is not a setting, and the next open asks again.
 *
 * Pure, so the grain is tested on the JVM rather than only through the screen.
 */
class PermissionOverride {

    /** The class whose question is on screen, or null. Observable: the dialog is drawn from it. */
    var asking: PermissionClass? by mutableStateOf(null)
        private set

    private var pending: CompletableDeferred<Boolean>? = null

    private var granted: Set<PermissionClass> = emptySet()

    /** True once the person has chosen to go on past [klass] in this document. */
    fun isGranted(klass: PermissionClass): Boolean = klass in granted

    /**
     * True when [klass] may go ahead: the document allows it ([allowed]), the person has already
     * said so for this document, or they say so now. Puts the question up and waits otherwise.
     *
     * Never two questions at once — a second one answers itself false rather than queueing,
     * the rule [PageRewriteQuestion]'s caller already keeps.
     */
    suspend fun permit(klass: PermissionClass, allowed: Boolean): Boolean {
        if (allowed || klass in granted) return true
        if (pending != null) return false
        val asked = CompletableDeferred<Boolean>()
        pending = asked
        asking = klass
        val proceed = try {
            asked.await()
        } finally {
            if (pending === asked) {
                pending = null
                asking = null
            }
        }
        if (proceed) granted = granted + klass
        return proceed
    }

    /** Continue or Cancel, from the screen. Does nothing when no question is up. */
    fun answer(proceed: Boolean) {
        pending?.complete(proceed)
    }

    /** The document closed: a question still up is answered Cancel, and the choices go with it. */
    fun reset() {
        pending?.complete(false)
        granted = emptySet()
    }
}
