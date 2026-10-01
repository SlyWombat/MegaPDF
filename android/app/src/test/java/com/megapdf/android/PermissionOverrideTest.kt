package com.megapdf.android

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The grain of the question (#558, ADR-004 decision 11): once per document per class of operation,
 * remembered while the document is open and never persisted.
 *
 * [PermissionPromptTest] drives the real dialog on a device; this is about the rule it keeps, which
 * is where it is cheap to say exactly what "once" means — including the two halves that are easy to
 * get wrong: Cancel must not count as an answer to remember, and one class's yes must not answer
 * another class's question.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PermissionOverrideTest {

    @Test
    fun `a document that allows the work is never asked about`() = runTest {
        val override = PermissionOverride()
        assertTrue(override.permit(PermissionClass.EDITING, allowed = true))
        assertNull("nothing should be on screen", override.asking)
        assertFalse("and nothing is remembered: there was nothing to remember", override.isGranted(PermissionClass.EDITING))
    }

    @Test
    fun `continuing is remembered, so the second change is not asked about`() = runTest {
        val override = PermissionOverride()
        val first = async { override.permit(PermissionClass.EDITING, allowed = false) }
        runCurrent()
        assertEquals(PermissionClass.EDITING, override.asking)
        override.answer(true)
        assertTrue(first.await())
        assertNull("the question comes down once answered", override.asking)

        assertTrue(override.permit(PermissionClass.EDITING, allowed = false))
        assertNull("and the second change asks nobody anything", override.asking)
    }

    @Test
    fun `cancelling is not an answer to remember, so the next attempt asks again`() = runTest {
        val override = PermissionOverride()
        val first = async { override.permit(PermissionClass.ASSEMBLY, allowed = false) }
        runCurrent()
        override.answer(false)
        assertFalse(first.await())
        assertFalse(override.isGranted(PermissionClass.ASSEMBLY))

        val second = async { override.permit(PermissionClass.ASSEMBLY, allowed = false) }
        runCurrent()
        assertEquals("a no is not a standing no", PermissionClass.ASSEMBLY, override.asking)
        override.answer(true)
        assertTrue(second.await())
    }

    @Test
    fun `each class is its own question`() = runTest {
        val override = PermissionOverride()
        val editing = async { override.permit(PermissionClass.EDITING, allowed = false) }
        runCurrent()
        override.answer(true)
        assertTrue(editing.await())

        // The author said "do not change this" and "do not rearrange the pages" separately, and may
        // well have meant them differently — so one yes does not answer for the other.
        val assembly = async { override.permit(PermissionClass.ASSEMBLY, allowed = false) }
        runCurrent()
        assertEquals(PermissionClass.ASSEMBLY, override.asking)
        override.answer(true)
        assertTrue(assembly.await())

        val extraction = async { override.permit(PermissionClass.EXTRACTION, allowed = false) }
        runCurrent()
        assertEquals(PermissionClass.EXTRACTION, override.asking)
        override.answer(false)
        assertFalse(extraction.await())
    }

    @Test
    fun `never two questions at once`() = runTest {
        val override = PermissionOverride()
        val first = async { override.permit(PermissionClass.EDITING, allowed = false) }
        runCurrent()
        // Refused rather than queued, the rule the page-rewrite warning's caller already keeps:
        // two dialogs would stack, and the second would be answering about work nobody can see.
        assertFalse(override.permit(PermissionClass.ASSEMBLY, allowed = false))
        assertEquals(PermissionClass.EDITING, override.asking)
        override.answer(true)
        assertTrue(first.await())
    }

    @Test
    fun `closing the document takes the choices with it`() = runTest {
        val override = PermissionOverride()
        val first = async { override.permit(PermissionClass.EDITING, allowed = false) }
        runCurrent()
        override.answer(true)
        assertTrue(first.await())
        assertTrue(override.isGranted(PermissionClass.EDITING))

        // Not a setting: a choice about this document dies with it, and the next open asks again.
        override.reset()
        assertFalse(override.isGranted(PermissionClass.EDITING))
    }

    @Test
    fun `a question still up when the document closes is answered Cancel`() = runTest {
        val override = PermissionOverride()
        val asked = async { override.permit(PermissionClass.ASSEMBLY, allowed = false) }
        runCurrent()
        override.reset()
        assertFalse("the change it was asking about went away with the document", asked.await())
    }
}
