package com.megapdf.android

import com.megapdf.android.HitTargetGeometry.Host
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

/**
 * The corner chip's touch host (#347): at least the target on each side, centred
 * on the chip, and never smaller than the chip itself.
 */
class HitTargetGeometryTest {
    @Test
    fun `a chip smaller than the target gets a target-sized host centred on it`() {
        // The ✕ chip at Pixel 6 density is about 26 x 22 dp; the target is 48 dp.
        assertEquals(Host(48, 48, -11, -13), HitTargetGeometry.centredHost(26, 22, 48))
    }

    @Test
    fun `a control already at or over the target keeps its own bounds`() {
        assertEquals(Host(60, 50, 0, 0), HitTargetGeometry.centredHost(60, 50, 48))
        assertEquals(Host(48, 48, 0, 0), HitTargetGeometry.centredHost(48, 48, 48))
    }

    @Test
    fun `only the short side grows`() {
        assertEquals(Host(60, 48, 0, -14), HitTargetGeometry.centredHost(60, 20, 48))
        assertEquals(Host(48, 70, -10, 0), HitTargetGeometry.centredHost(28, 70, 48))
    }

    @Test
    fun `the host's centre is the control's centre, to within a pixel`() {
        for (w in 1..80) for (h in 1..80) {
            val host = HitTargetGeometry.centredHost(w, h, 48)
            assertTrue("width $w", host.width >= w && host.width >= 48)
            assertTrue("height $h", host.height >= h && host.height >= 48)
            assertTrue("x-centre for $w x $h", abs((host.x + host.width / 2f) - w / 2f) <= 0.5f)
            assertTrue("y-centre for $w x $h", abs((host.y + host.height / 2f) - h / 2f) <= 0.5f)
        }
    }

    @Test
    fun `two chips on one edge share it without either host covering the other chip`() {
        // Chips 70 px wide at each end of a box, target 126 px.
        assertEquals(126, HitTargetGeometry.sharedHostWidth(400, 70, 126))   // room for both
        assertEquals(90, HitTargetGeometry.sharedHostWidth(160, 70, 126))    // what is left between them
        assertEquals(70, HitTargetGeometry.sharedHostWidth(97, 70, 126))     // the chip itself, no more
        for (box in 1..600) {
            val w = HitTargetGeometry.sharedHostWidth(box, 70, 126)
            val start = HitTargetGeometry.centredHost(70, 60, w, 126)          // ✎ hangs from x = 0
            val end = HitTargetGeometry.centredHost(70, 60, w, 126)            // ✕ hangs from x = box
            val startRight = start.x + start.width
            val endLeft = box - 70 + end.x
            assertTrue("box $box: start host ends at $startRight, end host begins at $endLeft",
                       box < 70 || startRight <= endLeft || w == 70)
            assertTrue("box $box: never narrower than the chip", w >= 70)
        }
    }

    @Test
    fun `the host is the box the issue measured`() {
        // #347: the accessibility node for the ✕ reported a 126 px box (316–442) on a
        // 2.625-density Pixel 6, which is 48 dp — the whole of it must now take the tap.
        val target = (48 * 2.625f).toInt()
        val host = HitTargetGeometry.centredHost(68, 58, target)
        assertEquals(126, host.width)
        assertEquals(126, host.height)
    }
}
