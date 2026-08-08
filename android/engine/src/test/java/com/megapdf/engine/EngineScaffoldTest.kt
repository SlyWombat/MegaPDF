package com.megapdf.engine

import org.junit.Assert.assertTrue
import org.junit.Test

// JVM unit tests can't load the .so; native coverage lives in instrumented tests (#13).
// This placeholder keeps :engine:testDebugUnitTest wired into CI from day one.
class EngineScaffoldTest {
    @Test
    fun `unit test toolchain runs`() {
        assertTrue(true)
    }
}
