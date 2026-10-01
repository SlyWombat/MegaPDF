package com.megapdf.android

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.megapdf.engine.DeclineReason
import com.megapdf.engine.Reflow
import com.megapdf.engine.ReflowBlock
import com.megapdf.engine.ReflowItem

/**
 * The #514 reflow spike's prototype, behind the app's debug flag. **Not a shipped screen.**
 *
 * What it is for: `docs/reading-mode-plan.md` §5's criterion 2 is the only one of the four that a
 * corpus cannot answer — whether contract 9's blocks, laid out as text, *read well* to a person, as
 * opposed to agreeing with `pdftotext` to three decimal places. That needs a real text view, on a
 * real phone, with real documents, read by somebody who did not write it. This is that view and
 * nothing more: no preferences, no find, no outline, no persistence, and deliberately no
 * translation (a debug-only screen is outside `docs/localisation.md` and `tools/capture-gate`, and
 * putting prototype strings through three locale files would be the wrong kind of thorough).
 *
 * The layout is the platform's, per plan §3's recommendation: `AnnotatedString` into Compose `Text`,
 * so bidi, CJK line breaking, hyphenation, selection and TalkBack's navigate-by-heading are
 * Android's and not ours. Heading levels go out as `semantics { heading() }`, which is the first
 * time a MegaPDF document is navigable by heading on a phone.
 *
 * Every decision about WHAT to show lives in `com.megapdf.engine.reflowOf`, not here: this file
 * renders a `Reflow` and makes no judgement of its own, which is the same split
 * `megapdf_write_text.cpp` uses ("inferring nothing itself").
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReflowScreen(
    reflow: Reflow?,
    /** A page's own raster, for a page shown as a picture; null while it is still being made. */
    pageImages: Map<Int, Bitmap>,
    /** A FIGURE's raster, keyed by the block's page and object index. */
    figureImages: Map<Pair<Int, Int>, Bitmap>,
    onClose: () -> Unit,
) {
    // Back leaves the reflow, one level, like the Pages grid's own handler — never straight out of
    // the document, which would ask about unsaved changes for a screen that changed nothing.
    androidx.activity.compose.BackHandler(onBack = onClose)
    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("Reflow (debug)")
                        if (reflow != null) {
                            val pct = (reflow.declineShare * 100).toInt()
                            Text(
                                "${reflow.items.size} items · ${reflow.declined.size} of " +
                                    "${reflow.pageCount} pages shown as pictures ($pct%)",
                                style = MaterialTheme.typography.labelSmall,
                            )
                        }
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onClose) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Leave reflow")
                    }
                },
            )
        },
    ) { padding ->
        if (reflow == null) {
            Column(
                modifier = Modifier.fillMaxSize().padding(padding),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally,
            ) {
                CircularProgressIndicator()
                Text("Reading the document's structure", modifier = Modifier.padding(16.dp))
            }
            return@Scaffold
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding),
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
        ) {
            items(reflow.items) { item -> ReflowItemView(item, reflow, pageImages, figureImages) }
        }
    }
}

@Composable
private fun ReflowItemView(
    item: ReflowItem,
    reflow: Reflow,
    pageImages: Map<Int, Bitmap>,
    figureImages: Map<Pair<Int, Int>, Bitmap>,
) {
    when (item) {
        is ReflowItem.Heading -> Text(
            text = annotated(item.block),
            // size_ratio is the block's own size over the range's body size, so a heading is as
            // much bigger than the body text as the document drew it, rather than as big as a level
            // number guesses. Clamped: a title page's 40 pt over a 9 pt body is a 4.4x heading, and
            // nothing that big is readable on a phone.
            fontSize = (16.0 * headingScale(item.block)).sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 20.dp, bottom = 6.dp)
                .semantics { heading() },
        )

        is ReflowItem.Paragraph -> Text(
            text = annotated(item.block),
            fontSize = 16.sp,
            // `continues` means this block is the back half of a paragraph a column or page break
            // split. No gap above it, so the reader sees one paragraph, which is the whole value
            // contract 9's `continues` field carries.
            modifier = Modifier.fillMaxWidth().padding(top = if (item.joinsPrevious) 0.dp else 10.dp),
        )

        is ReflowItem.ListItem -> Row(modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
            Text(
                text = item.marker.ifEmpty { "•" },
                fontSize = 16.sp,
                modifier = Modifier
                    .padding(start = (16 * (item.block.level - 1).coerceAtLeast(0)).dp)
                    .width(28.dp),
            )
            Text(text = annotated(item.block), fontSize = 16.sp)
        }

        is ReflowItem.TableRow -> Column(modifier = Modifier.fillMaxWidth()) {
            Row(modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                item.cells.forEach { cell ->
                    Text(
                        text = cell,
                        fontSize = 14.sp,
                        fontWeight = if (item.isHeaderRow) FontWeight.Bold else FontWeight.Normal,
                        modifier = Modifier.weight(1f).padding(end = 8.dp),
                    )
                }
            }
            HorizontalDivider()
        }

        is ReflowItem.Figure -> Column(modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
            val bmp = figureImages[item.block.page to item.block.objectIndex]
            if (bmp != null) {
                Image(
                    bitmap = bmp.asImageBitmap(),
                    // The /Alt text when the page was tagged (#358); otherwise say what it is and
                    // where it came from rather than leaving a screen reader with nothing.
                    contentDescription = item.alt.ifEmpty { "Figure on page ${item.block.page + 1}" },
                    contentScale = ContentScale.FillWidth,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            if (item.alt.isNotEmpty()) {
                Text(item.alt, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 4.dp))
            }
        }

        is ReflowItem.PageAsImage -> Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 14.dp)
                .background(MaterialTheme.colorScheme.surfaceVariant),
        ) {
            // The note is the honest part, and plan §7 says the copy must say WHY: a page shown as
            // a picture with no explanation is read as the feature having lost the page.
            Text(
                text = when (item.reason) {
                    DeclineReason.FORM ->
                        "Page ${item.page + 1} is a form, so it is shown as the page. Reflow never fills fields."
                    DeclineReason.NO_TEXT_LAYER ->
                        "Page ${item.page + 1} has no text layer, so it is shown as the page."
                    DeclineReason.LOW_CONFIDENCE ->
                        "Page ${item.page + 1}'s layout could not be read reliably, so it is shown as the page."
                },
                style = MaterialTheme.typography.labelMedium,
                modifier = Modifier.padding(8.dp),
            )
            val bmp = pageImages[item.page]
            if (bmp != null) {
                Image(
                    bitmap = bmp.asImageBitmap(),
                    contentDescription = "Page ${item.page + 1}",
                    contentScale = ContentScale.FillWidth,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            // A form's filled values are still read out, so a screen reader gets them even though
            // the page itself is a picture (plan §2 tier 3's forms rule).
            item.fieldValues.filter { it.second.isNotEmpty() }.forEach { (name, value) ->
                Text(
                    text = "$name: $value",
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 2.dp),
                )
            }
        }
    }
}

/**
 * A block's spans as one `AnnotatedString`. The block's text is exactly the concatenation of its
 * spans (SDD §6.2 contract 6), so building the string from the spans cannot disagree with
 * `MEGAPDF_BLOCK_TEXT` — and it is the only way to keep the bold, italic and monospace runs.
 *
 * The span's own font family (`megapdf_block_span_font`, #514) is NOT matched to an installed face
 * here. The spike's question about fonts was whether name-and-style matching is enough, and the
 * honest prototype answer is the one plan §7 prescribes as the fallback: pick the platform serif,
 * sans or monospace by the span's flags. A subset-embedded name ("ABCDEF+Minion-Regular") matches
 * nothing installed on a phone anyway, so matching it would mostly produce the same result while
 * hiding how often it failed.
 */
/** A heading's size relative to the body, from the block's own `size_ratio`, clamped to 1.0..2.0. */
private fun headingScale(block: ReflowBlock): Double {
    val ratio = block.spans.firstOrNull()?.sizeRatio ?: return 1.3
    if (ratio <= 0.0 || ratio.isNaN()) return 1.3
    return ratio.coerceIn(1.0, 2.0)
}

private fun annotated(block: ReflowBlock) = buildAnnotatedString {
    if (block.spans.isEmpty()) {
        append(block.text)
        return@buildAnnotatedString
    }
    for (s in block.spans) {
        withStyle(
            SpanStyle(
                fontWeight = if (s.bold) FontWeight.Bold else FontWeight.Normal,
                fontStyle = if (s.italic) FontStyle.Italic else FontStyle.Normal,
                fontFamily = if (s.monospace) FontFamily.Monospace else FontFamily.Default,
                textDecoration = if (s.link) TextDecoration.Underline else null,
            ),
        ) {
            append(s.text)
        }
    }
}
