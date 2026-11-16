package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
import androidx.test.espresso.intent.matcher.IntentMatchers.hasData
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * About names both places a person can go (#730): the support page and the source code. Each
 * row is a button that opens the system browser, so the test answers the VIEW intent itself
 * (no browser is launched) and reads the address off the intent that was sent.
 */
@RunWith(AndroidJUnit4::class)
class AboutLinksTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheBrowser() {
        Intents.init()
        intending(hasAction(Intent.ACTION_VIEW))
            .respondWith(Instrumentation.ActivityResult(Activity.RESULT_OK, null))
    }

    @After
    fun releaseTheSystem() = Intents.release()

    @Test
    fun aboutOffersSupportAndSourceCodeAndEachOpensItsAddress() {
        assertEquals("https://megapdf.ca/support/", SUPPORT_URL)
        assertEquals("https://github.com/SlyWombat/MegaPDF", SOURCE_URL)

        rule.onNodeWithContentDescription(str(R.string.about_megapdf)).performClick()
        rule.onNodeWithText(str(R.string.support_link)).assertExists()
        rule.onNodeWithText(str(R.string.source_link)).assertExists()

        rule.onNodeWithText(str(R.string.support_link)).performClick()
        intended(hasAction(Intent.ACTION_VIEW))
        intended(hasData(SUPPORT_URL))

        rule.onNodeWithText(str(R.string.source_link)).performClick()
        intended(hasData(SOURCE_URL))
    }
}
