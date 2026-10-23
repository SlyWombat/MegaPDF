package com.megapdf.engine

/**
 * A PDF worth shrinking (#593), built here rather than checked in: [pages] pages, each drawing one
 * 300 × 300 px DeviceRGB 8 bpc picture, uncompressed (270,000 bytes), at 72 × 72 pt. Its 150 dpi
 * target is 150 px, so the core's rules call it oversized and re-encode it. The picture is a
 * blue (R 30–61, G 70–101, B 185–216), so a red/blue swap in the encoder shows as orange.
 *
 * Noisy, not flat: a save Flate-compresses an uncompressed picture, and a flat one comes out at
 * 289 bytes, under the 8 KB floor, so a shrink of the saved copy finds nothing to do (measured
 * on kdocker3 for #593). This one is still about 224 KB after a save.
 *
 * The app's instrumented tests have a twin of this object (`android/app/src/androidTest/…/
 * LargeImagePdf.kt`): the two test source sets cannot see each other.
 */
object LargeImagePdf {
    const val SIDE = 300

    fun bytes(pages: Int): ByteArray {
        // Noise over the blue, so a save's Flate cannot squeeze it under the size floors (#593).
        var seed = 593
        val pixels = String(CharArray(SIDE * SIDE * 3) { i ->
            seed = seed * 1103515245 + 12345
            val noise = (seed ushr 16) and 31
            when (i % 3) { 0 -> (30 + noise).toChar(); 1 -> (70 + noise).toChar(); else -> (185 + noise).toChar() }
        })
        val objects = ArrayList<String>()
        val kids = (0 until pages).joinToString(" ") { "${3 + it * 3} 0 R" }
        objects += "<< /Type /Catalog /Pages 2 0 R >>"
        objects += "<< /Type /Pages /Kids [$kids] /Count $pages >>"
        for (index in 0 until pages) {
            val page = 3 + index * 3
            objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /XObject << /Im1 ${page + 1} 0 R >> >> /Contents ${page + 2} 0 R >>"
            objects += "<< /Type /XObject /Subtype /Image /Width $SIDE /Height $SIDE /ColorSpace /DeviceRGB " +
                "/BitsPerComponent 8 /Length ${pixels.length} >>\nstream\n$pixels\nendstream"
            val body = "q 72 0 0 72 100 600 cm /Im1 Do Q"
            objects += "<< /Length ${body.length} >>\nstream\n$body\nendstream"
        }
        val out = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>()
        objects.forEachIndexed { i, body ->
            offsets += out.length
            out.append("${i + 1} 0 obj\n").append(body).append("\nendobj\n")
        }
        val xref = out.length
        out.append("xref\n0 ${objects.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) out.append(offset.toString().padStart(10, '0')).append(" 00000 n \n")
        out.append("trailer\n<< /Size ${objects.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        // Latin-1, so every character is one byte and every offset above is a byte offset.
        return out.toString().toByteArray(Charsets.ISO_8859_1)
    }
}
