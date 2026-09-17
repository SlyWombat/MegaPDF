package com.megapdf.android

import android.graphics.Bitmap
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.materialIcon
import androidx.compose.material.icons.materialPath
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

/**
 * The signature library as a bottom sheet (#99).
 *
 * The page stays visible above the sheet, each signature shows its actual ink on
 * a white card so the user can tell them apart, tapping a card arms placement
 * (the viewer's "tap the page" banner takes over from there), and the two ways
 * to add one are real buttons rather than a row of purple text. Delete asks
 * once; rename is on the same card menu, since a library of "Signature 1..4"
 * is otherwise a guessing game.
 *
 * Thumbnails are decoded through [loadBitmap] off the main thread and cached
 * per entry id for the life of the sheet, so scrolling the grid never blocks.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SignaturesSheet(
    signatures: List<SignatureEntry>,
    loadBitmap: suspend (SignatureEntry) -> Bitmap?,
    onPick: (SignatureEntry) -> Unit,
    onDraw: () -> Unit,
    onType: () -> Unit,
    onAddFromPhoto: () -> Unit,
    onRename: (id: String, name: String) -> Unit,
    onDelete: (id: String) -> Unit,
    onDismiss: () -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    // Hide with the sheet's own animation, then tell the owner it is gone.
    // Removing the composable outright would cut the slide-out short.
    val dismissThen: (() -> Unit) -> Unit = { after ->
        scope.launch { sheetState.hide() }.invokeOnCompletion { onDismiss(); after() }
    }

    val thumbnails = remember { mutableStateMapOf<String, Bitmap?>() }
    var confirmDelete by remember { mutableStateOf<SignatureEntry?>(null) }
    var renaming by remember { mutableStateOf<SignatureEntry?>(null) }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp)
                .navigationBarsPadding(),
        ) {
            Text(
                stringResource(R.string.signatures),
                style = MaterialTheme.typography.titleLarge,
            )
            Spacer(Modifier.height(16.dp))

            if (signatures.isEmpty()) {
                Surface(
                    shape = RoundedCornerShape(12.dp),
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(
                        stringResource(R.string.no_signatures_yet),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(16.dp),
                    )
                }
            } else {
                // Two across on a phone: a signature is wide and short, so a
                // 2-up card at ~72 dp shows the whole stroke. Bounded so a large
                // library scrolls inside the sheet instead of growing it off
                // the top of the screen.
                LazyVerticalGrid(
                    columns = GridCells.Fixed(2),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                    contentPadding = PaddingValues(bottom = 4.dp),
                    modifier = Modifier.heightIn(max = 336.dp),
                ) {
                    items(signatures, key = { it.id }) { entry ->
                        LaunchedEffect(entry.id) {
                            if (!thumbnails.containsKey(entry.id)) {
                                thumbnails[entry.id] = loadBitmap(entry)
                            }
                        }
                        SignatureCard(
                            entry = entry,
                            thumbnail = thumbnails[entry.id],
                            loaded = thumbnails.containsKey(entry.id),
                            onPick = { dismissThen { onPick(entry) } },
                            onRename = { renaming = entry },
                            onDelete = { confirmDelete = entry },
                        )
                    }
                }
            }

            Spacer(Modifier.height(24.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                FilledTonalButton(
                    onClick = { dismissThen(onDraw) },
                    modifier = Modifier.weight(1f),
                    contentPadding = PaddingValues(horizontal = 8.dp, vertical = 10.dp),
                ) {
                    // Icon above the label: three of these share a phone's width in
                    // French too ("Dessiner" / "Taper" / "Photo"), where a row layout
                    // truncated the first word.
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(Icons.Filled.Edit, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.height(4.dp))
                        Text(stringResource(R.string.draw), maxLines = 1, style = MaterialTheme.typography.labelLarge)
                    }
                }
                FilledTonalButton(
                    onClick = { dismissThen(onType) },
                    modifier = Modifier.weight(1f),
                    contentPadding = PaddingValues(horizontal = 8.dp, vertical = 10.dp),
                ) {
                    // Icon above the label: three of these share a phone's width in
                    // French too ("Dessiner" / "Taper" / "Photo"), where a row layout
                    // truncated the first word.
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(KeyboardIcon, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.height(4.dp))
                        Text(stringResource(R.string.type_signature), maxLines = 1, style = MaterialTheme.typography.labelLarge)
                    }
                }
                FilledTonalButton(
                    onClick = { dismissThen(onAddFromPhoto) },
                    modifier = Modifier.weight(1f),
                    contentPadding = PaddingValues(horizontal = 8.dp, vertical = 10.dp),
                ) {
                    // Icon above the label: three of these share a phone's width in
                    // French too ("Dessiner" / "Taper" / "Photo"), where a row layout
                    // truncated the first word.
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(PhotoIcon, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.height(4.dp))
                        Text(stringResource(R.string.add_from_photos), maxLines = 1, style = MaterialTheme.typography.labelLarge)
                    }
                }
            }
            Spacer(Modifier.height(24.dp))
        }
    }

    confirmDelete?.let { entry ->
        AlertDialog(
            onDismissRequest = { confirmDelete = null },
            title = { Text(stringResource(R.string.signature_delete_title, entry.displayName)) },
            text = { DialogBody { Text(stringResource(R.string.signature_delete_body)) } },
            confirmButton = {
                TextButton(onClick = { confirmDelete = null; onDelete(entry.id) }) {
                    Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmDelete = null }) { Text(stringResource(R.string.cancel)) }
            },
        )
    }

    renaming?.let { entry ->
        var name by remember(entry.id) { mutableStateOf(entry.displayName) }
        AlertDialog(
            modifier = Modifier.imePadding(),
            onDismissRequest = { renaming = null },
            title = { Text(stringResource(R.string.signature_rename_title)) },
            text = {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.signature_name)) },
                    modifier = Modifier.fillMaxWidth(),
                )
            },
            confirmButton = {
                TextButton(
                    onClick = { renaming = null; onRename(entry.id, name.trim()) },
                    enabled = name.isNotBlank() && name.trim() != entry.displayName,
                ) { Text(stringResource(R.string.save)) }
            },
            dismissButton = {
                TextButton(onClick = { renaming = null }) { Text(stringResource(R.string.cancel)) }
            },
        )
    }
}

/**
 * One library entry: the ink on a white card (a signature is drawn on paper, so
 * the card stays white in dark theme too), the name beneath, and a small menu
 * for the two things you rarely do. Tap places; long-press opens the same menu
 * for anyone who does not spot the dots.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun SignatureCard(
    entry: SignatureEntry,
    thumbnail: Bitmap?,
    loaded: Boolean,
    onPick: () -> Unit,
    onRename: () -> Unit,
    onDelete: () -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }
    val a11y = stringResource(R.string.signature_card_a11y, entry.displayName)
    val placeLabel = stringResource(R.string.signature_place)
    val optionsLabel = stringResource(R.string.signature_options, entry.displayName)

    Column(
        Modifier
            .fillMaxWidth()
            .semantics(mergeDescendants = true) { contentDescription = a11y },
    ) {
        Surface(
            shape = RoundedCornerShape(12.dp),
            color = Color.White,
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
            modifier = Modifier
                .fillMaxWidth()
                .height(72.dp)
                .combinedClickable(
                    onClick = onPick,
                    onClickLabel = placeLabel,
                    onLongClick = { menuOpen = true },
                    onLongClickLabel = optionsLabel,
                ),
        ) {
            Box(Modifier.fillMaxSize().padding(horizontal = 12.dp, vertical = 8.dp), contentAlignment = Alignment.Center) {
                when {
                    thumbnail != null -> Image(
                        bitmap = thumbnail.asImageBitmap(),
                        contentDescription = null,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize(),
                    )
                    !loaded -> CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                    else -> Text(
                        stringResource(R.string.signature_image_missing),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                entry.displayName,
                style = MaterialTheme.typography.labelLarge,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f).padding(start = 4.dp),
            )
            Box {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(Icons.Filled.MoreVert, contentDescription = optionsLabel)
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.signature_rename)) },
                        onClick = { menuOpen = false; onRename() },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) },
                        onClick = { menuOpen = false; onDelete() },
                    )
                }
            }
        }
    }
}

/**
 * The Material "image" glyph. `material-icons-core` has no photo icon and one
 * glyph is not worth `material-icons-extended` (same reasoning as the undo
 * arrow in ViewerScreen.kt), so it is drawn from the standard 24dp path.
 */
/** The Material "keyboard" glyph for Type, drawn from its standard 24dp path for the same reason. */
private val KeyboardIcon: ImageVector = materialIcon(name = "Filled.Keyboard") {
    materialPath {
        moveTo(20.0f, 5.0f)
        horizontalLineTo(4.0f)
        curveToRelative(-1.1f, 0.0f, -1.99f, 0.9f, -1.99f, 2.0f)
        lineTo(2.0f, 17.0f)
        curveToRelative(0.0f, 1.1f, 0.9f, 2.0f, 2.0f, 2.0f)
        horizontalLineToRelative(16.0f)
        curveToRelative(1.1f, 0.0f, 2.0f, -0.9f, 2.0f, -2.0f)
        verticalLineTo(7.0f)
        curveToRelative(0.0f, -1.1f, -0.9f, -2.0f, -2.0f, -2.0f)
        close()
        moveTo(11.0f, 8.0f)
        horizontalLineToRelative(2.0f)
        verticalLineToRelative(2.0f)
        horizontalLineToRelative(-2.0f)
        close()
        moveTo(11.0f, 11.0f)
        horizontalLineToRelative(2.0f)
        verticalLineToRelative(2.0f)
        horizontalLineToRelative(-2.0f)
        close()
        moveTo(8.0f, 8.0f)
        horizontalLineToRelative(2.0f)
        verticalLineToRelative(2.0f)
        horizontalLineTo(8.0f)
        close()
        moveTo(8.0f, 11.0f)
        horizontalLineToRelative(2.0f)
        verticalLineToRelative(2.0f)
        horizontalLineTo(8.0f)
        close()
        moveTo(7.0f, 13.0f)
        horizontalLineTo(5.0f)
        verticalLineToRelative(-2.0f)
        horizontalLineToRelative(2.0f)
        close()
        moveTo(7.0f, 10.0f)
        horizontalLineTo(5.0f)
        verticalLineTo(8.0f)
        horizontalLineToRelative(2.0f)
        close()
        moveTo(16.0f, 17.0f)
        horizontalLineTo(8.0f)
        verticalLineToRelative(-2.0f)
        horizontalLineToRelative(8.0f)
        close()
        moveTo(16.0f, 13.0f)
        horizontalLineToRelative(-2.0f)
        verticalLineToRelative(-2.0f)
        horizontalLineToRelative(2.0f)
        close()
        moveTo(16.0f, 10.0f)
        horizontalLineToRelative(-2.0f)
        verticalLineTo(8.0f)
        horizontalLineToRelative(2.0f)
        close()
        moveTo(19.0f, 13.0f)
        horizontalLineToRelative(-2.0f)
        verticalLineToRelative(-2.0f)
        horizontalLineToRelative(2.0f)
        close()
        moveTo(19.0f, 10.0f)
        horizontalLineToRelative(-2.0f)
        verticalLineTo(8.0f)
        horizontalLineToRelative(2.0f)
        close()
    }
}

private val PhotoIcon: ImageVector = materialIcon(name = "Filled.Image") {
    materialPath {
        moveTo(21.0f, 19.0f)
        verticalLineTo(5.0f)
        curveToRelative(0.0f, -1.1f, -0.9f, -2.0f, -2.0f, -2.0f)
        horizontalLineTo(5.0f)
        curveToRelative(-1.1f, 0.0f, -2.0f, 0.9f, -2.0f, 2.0f)
        verticalLineToRelative(14.0f)
        curveToRelative(0.0f, 1.1f, 0.9f, 2.0f, 2.0f, 2.0f)
        horizontalLineToRelative(14.0f)
        curveToRelative(1.1f, 0.0f, 2.0f, -0.9f, 2.0f, -2.0f)
        close()
        moveTo(8.5f, 13.5f)
        lineToRelative(2.5f, 3.01f)
        lineTo(14.5f, 12.0f)
        lineToRelative(4.5f, 6.0f)
        horizontalLineTo(5.0f)
        lineToRelative(3.5f, -4.5f)
        close()
    }
}
