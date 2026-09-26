package com.megapdf.android

import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.imePadding
import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import kotlin.math.roundToInt
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import com.megapdf.android.ui.Brand
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.OutlinedTextField
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.material3.BottomAppBar
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.PlainTooltip
import androidx.compose.material3.TooltipBox
import androidx.compose.material3.TooltipDefaults
import androidx.compose.material3.rememberTooltipState
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource

// Zoom is a multiple of fit-to-width, not of the page's own size: the page is
// laid out at the container's width, so 1f fills it edge to edge and is also the
// floor. There is no window to land on and no "100%" to choose, which is why the
// store captures pose at 1f and need nothing to pin them — every capture is a
// fresh process, and this is where it starts (#146).
private const val MIN_ZOOM = 1f
private const val MAX_ZOOM = 4f

// A redaction drag, as a fraction of the page (#173). MIN_MARK_EXTENT is what tells a
// drag from a tap; MIN_MARK_THICKNESS is what a flat drag along a line is grown to, so
// the band covers the line rather than a hairline through the middle of it. About 8 pt
// on US Letter, which is the ink height of a line of body text.
private const val MIN_MARK_EXTENT = 0.005f
private const val MIN_MARK_THICKNESS = 0.01f

// Search highlight fills (#26): every match gets translucent brand cyan; the
// current match is set apart in translucent brand blue. Amber used to carry the
// current match, which read well but is not a colour MegaPDF owns
// (docs/design-tokens.md §1.2).
private val REDACTION_MARK = Brand.RedactionMark

/** Which of the two flows the unsaved-changes prompt (#145, #378) was asked on behalf of. */
private enum class UnsavedAction { CLOSE, SHARE }
private val REDACTION_MARK_OUTLINE = Brand.RedactionMarkOutline
private val MATCH_HIGHLIGHT = Brand.FindMatch
private val CURRENT_MATCH_HIGHLIGHT = Brand.FindMatchCurrent

@OptIn(ExperimentalMaterial3Api::class, kotlinx.coroutines.FlowPreview::class)
@Composable
fun ViewerScreen(
    displayName: String,
    pageSizes: List<PageSize>,
    pageBitmaps: Map<Int, Bitmap>,
    isDirty: Boolean,
    isSaving: Boolean,
    signatures: List<SignatureEntry>,
    selectedStamp: SelectedStamp?,
    selectedTextBox: SelectedTextBox?,
    canUndo: Boolean,
    canRedo: Boolean,
    pendingTextTap: PendingTextTap?,
    pendingBodyEdit: PendingBodyEdit? = null,
    notice: String? = null,
    searchQuery: String,
    searchHits: List<SearchHit>,
    currentHitIndex: Int,
    isSearching: Boolean,
    onRenderWindowChange: (firstVisible: Int, lastVisible: Int, targetWidthPx: Int) -> Unit,
    onPageTap: (pageIndex: Int, xFraction: Float, yFraction: Float) -> Unit,
    onSearchQueryChange: (String) -> Unit,
    onSearchPrevious: () -> Unit,
    onSearchNext: () -> Unit,
    onCloseSearch: () -> Unit,
    onStartPlacement: (SignatureEntry) -> Unit,
    onAddSignature: () -> Unit,
    onSaveDrawnSignature: (Bitmap) -> Unit,
    onDeleteSignature: (String) -> Unit,
    onRenameSignature: (id: String, name: String) -> Unit,
    loadSignatureBitmap: suspend (SignatureEntry) -> Bitmap?,
    screenshotSheet: String? = null,
    onUndo: () -> Unit,
    onRedo: () -> Unit,
    onStartTextPlacement: () -> Unit,
    onCommitText: (text: String, fontSize: Double, fontName: String) -> Unit,
    onCancelTextPlacement: () -> Unit,
    onCommitBodyEdit: (String) -> Unit = {},
    onCancelBodyEdit: () -> Unit = {},
    onCommitStampRect: (com.megapdf.engine.PdfRect) -> Unit,
    onRemoveStamp: () -> Unit,
    onCommitTextBoxRect: (com.megapdf.engine.PdfRect) -> Unit,
    onEditTextBox: () -> Unit,
    onRemoveTextBox: () -> Unit,
    onSave: () -> Unit,
    onSaveAs: () -> Unit,
    // Redaction (SDD §3.8 / F7, #173). Marks are not page content: the core keeps them and
    // never writes them, so the screen draws them and nothing here is in the raster.
    redactMode: Boolean = false,
    redactionMarks: Map<Int, List<com.megapdf.engine.RedactionMark>> = emptyMap(),
    onToggleRedact: () -> Unit = {},
    onMarkForRedaction: (pageIndex: Int, rect: com.megapdf.engine.PdfRect) -> Unit = { _, _ -> },
    // A mark is a thing the user put on the page, so it can be tapped, moved, resized and
    // removed like a stamp (#329).
    selectedRedactionMark: SelectedRedactionMark? = null,
    onSelectRedactionMark: (pageIndex: Int, markId: Int) -> Unit = { _, _ -> },
    onRemoveRedactionMark: (pageIndex: Int, markId: Int) -> Unit = { _, _ -> },
    onClearRedactionMarks: () -> Unit = {},
    onCommitRedactionMarkRect: (pageIndex: Int, markId: Int, rect: com.megapdf.engine.PdfRect) -> Unit =
        { _, _, _ -> },
    // Document security (#131).
    capabilities: DocumentCapabilities = DocumentCapabilities.FULL,
    hasDocumentFile: Boolean = false,
    unlockPrompt: UnlockPrompt? = null,
    passwordPrompt: PasswordCommandMode? = null,
    onStartUnlock: () -> Unit = {},
    onUnlock: (String) -> Unit = {},
    onCancelUnlock: () -> Unit = {},
    onStartPasswordCommand: () -> Unit = {},
    onSetPassword: (String) -> Unit = {},
    onRemovePassword: () -> Unit = {},
    onCancelPasswordCommand: () -> Unit = {},
    // The warning before the first text-box change on a page that regenerating would alter (#139).
    pageRewriteWarningShown: Boolean = false,
    onAnswerPageRewrite: (proceed: Boolean) -> Unit = {},
    onClose: () -> Unit,
    // Busy feedback (#145): the strip and the page spinner, and what they disable.
    busy: BusyState? = null,
    /** A change is still going in or a save runs: commits wait, drags are off. */
    editingBlocked: Boolean = false,
    /** The editing tools show disabled: a save runs, or page work has shown its spinner. */
    toolsDisabled: Boolean = false,
    onCurrentPageChange: (pageIndex: Int) -> Unit = {},
    /** Save from the unsaved-changes prompt, closing once saved. */
    onSaveAndClose: () -> Unit = onSave,
    // Share (#378): hands the document to the OS share sheet.
    onShare: () -> Unit = {},
    /** Save from the unsaved-changes prompt, sharing once saved. */
    onSaveAndShare: () -> Unit = onShare,
    /** Discard from the unsaved-changes prompt: shares the last-saved file, not the
     *  pending edits — the open document keeps them, exactly as Cancel would leave it. */
    onShareLastSaved: () -> Unit = onShare,
) {
    var zoom by remember { mutableFloatStateOf(1f) }
    // The rubber band a redaction drag is drawing; null the rest of the time (#173).
    var redactBand: RedactBand? by remember { mutableStateOf(null) }
    val listState = rememberLazyListState()
    // Hoisted so search navigation can reach a hit that is off to the side when zoomed.
    val hScroll = rememberScrollState()
    var menuOpen by remember { mutableStateOf(false) }
    var aboutOpen by remember { mutableStateOf(false) }
    var noticesOpen by remember { mutableStateOf(false) }
    // The unsaved-changes prompt (#145, #378): one dialog, asked before either Close or
    // Share proceeds with a dirty document. pendingUnsavedAction records which of the two
    // asked, so Save/Discard/Cancel resolve to the right pair of callbacks.
    var pendingUnsavedAction by remember { mutableStateOf<UnsavedAction?>(null) }
    var signDialogOpen by remember { mutableStateOf(false) }
    var searchOpen by remember { mutableStateOf(false) }
    // #145: while a save or password change runs, the document can't be closed and its file
    // commands wait.
    val documentLocked = busy?.locksDocument == true
    val closeSearch = { searchOpen = false; onCloseSearch() }
    val requestClose = { if (isDirty) pendingUnsavedAction = UnsavedAction.CLOSE else onClose() }
    val requestShare = { if (isDirty) pendingUnsavedAction = UnsavedAction.SHARE else onShare() }
    // Back stays handled while locked, so the system can't finish the activity under a save.
    androidx.activity.compose.BackHandler {
        if (searchOpen) closeSearch() else if (!documentLocked) requestClose()
    }

    var drawDialogOpen by remember { mutableStateOf(false) }
    var typeDialogOpen by remember { mutableStateOf(false) }
    LaunchedEffect(screenshotSheet) {
        if (screenshotSheet == "sign") signDialogOpen = true
        if (screenshotSheet == "draw") drawDialogOpen = true
        // The query itself is already seeded by the view model, so the bar
        // opens filled in, with its match count and highlights in place.
        if (screenshotSheet == "search") searchOpen = true
    }
    if (signDialogOpen) {
        // A bottom sheet, not a dialog (#99): the page stays visible above it and
        // each entry shows its ink. The sheet hides itself before calling back.
        SignaturesSheet(
            signatures = signatures,
            loadBitmap = loadSignatureBitmap,
            onPick = onStartPlacement,
            onDraw = { drawDialogOpen = true },
            onType = { typeDialogOpen = true },
            onAddFromPhoto = onAddSignature,
            onRename = onRenameSignature,
            onDelete = onDeleteSignature,
            onDismiss = { signDialogOpen = false },
        )
    }
    if (pendingTextTap != null) {
        // A correction opens on the box's current text, size and face (#36/#43);
        // a new box opens empty, at whatever the last one used.
        val editing = pendingTextTap.editingId != null
        var typed by remember(pendingTextTap) { mutableStateOf(pendingTextTap.initialText) }
        var size by remember(pendingTextTap) { mutableStateOf(pendingTextTap.fontSize) }
        var face by remember(pendingTextTap) { mutableStateOf(pendingTextTap.fontName) }
        AlertDialog(
            modifier = Modifier.imePadding(),
            onDismissRequest = onCancelTextPlacement,
            title = { Text(stringResource(if (editing) R.string.edit_text else R.string.add_text)) },
            text = {
                DialogBody(spacing = 12) {
                    Text(
                        stringResource(
                            if (editing) R.string.edit_text_hint else R.string.add_text_hint
                        )
                    )
                    OutlinedTextField(
                        value = typed,
                        onValueChange = { typed = it },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    ChipRow(
                        label = stringResource(R.string.size),
                        options = TEXT_SIZES,
                        selected = size,
                        labelOf = { it.toInt().toString() },
                        onSelect = { size = it },
                    )
                    ChipRow(
                        label = stringResource(R.string.font),
                        options = com.megapdf.engine.STANDARD_FONTS,
                        selected = face,
                        labelOf = ::fontLabel,
                        onSelect = { face = it },
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = { onCommitText(typed, size, face) },
                    // Waits, keeping what was typed, while another change is still going in (#145).
                    enabled = typed.isNotBlank() && !editingBlocked,
                ) {
                    Text(stringResource(if (editing) R.string.save else R.string.add))
                }
            },
            dismissButton = {
                TextButton(onClick = onCancelTextPlacement) { Text(stringResource(R.string.cancel)) }
            },
        )
    }

    if (pendingBodyEdit != null) {
        // The document's own text (#114): one field, and the line keeps its size and
        // font (SDD §3.1 — no formatting controls). Clearing the field removes the line.
        var typed by remember(pendingBodyEdit) { mutableStateOf(pendingBodyEdit.initialText) }
        AlertDialog(
            modifier = Modifier.imePadding(),
            onDismissRequest = onCancelBodyEdit,
            title = { Text(stringResource(R.string.edit_text)) },
            text = {
                DialogBody(spacing = 12) {
                    Text(stringResource(R.string.body_text_hint))
                    OutlinedTextField(
                        value = typed,
                        onValueChange = { typed = it },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = { onCommitBodyEdit(typed) }, enabled = !editingBlocked) {
                    Text(stringResource(R.string.save))
                }
            },
            dismissButton = {
                TextButton(onClick = onCancelBodyEdit) { Text(stringResource(R.string.cancel)) }
            },
        )
    }

    if (notice != null) {
        // A one-line notice over the page that goes away on its own — for things the
        // user should know but need not act on, like a substituted font (#114).
        // Lifted clear of the bottom toolbar (#144): the bar's 80dp, the navigation bar
        // under it, and a little air.
        val density = LocalDensity.current
        val navigationBar = androidx.compose.foundation.layout.WindowInsets.navigationBars.getBottom(density)
        val lift = with(density) { 96.dp.roundToPx() } + navigationBar
        androidx.compose.ui.window.Popup(
            alignment = androidx.compose.ui.Alignment.BottomCenter,
            offset = androidx.compose.ui.unit.IntOffset(0, -lift),
        ) {
            androidx.compose.material3.Surface(
                shape = androidx.compose.foundation.shape.RoundedCornerShape(24.dp),
                color = androidx.compose.material3.MaterialTheme.colorScheme.inverseSurface,
                shadowElevation = 6.dp,
            ) {
                Text(
                    notice,
                    color = androidx.compose.material3.MaterialTheme.colorScheme.inverseOnSurface,
                    style = androidx.compose.material3.MaterialTheme.typography.bodyMedium,
                    modifier = Modifier
                        .padding(horizontal = 16.dp, vertical = 10.dp)
                        .widthIn(max = 360.dp),
                )
            }
        }
    }

    if (drawDialogOpen) {
        DrawSignatureDialog(
            onSave = onSaveDrawnSignature,
            onDismiss = { drawDialogOpen = false; signDialogOpen = true },
            screenshotMode = screenshotSheet == "draw",
        )
    }

    if (typeDialogOpen) {
        // Typed names take the drawn-signature path (#101): a transparent bitmap
        // that gets trimmed to its ink and stored like any other.
        TypeSignatureDialog(
            onSave = { typeDialogOpen = false; signDialogOpen = true; onSaveDrawnSignature(it) },
            onDismiss = { typeDialogOpen = false; signDialogOpen = true },
        )
    }

    if (unlockPrompt != null) {
        // A restricted document's owner password (#131). Reopening from the file drops
        // unsaved changes, so the dialog says so when there are any.
        UnlockDialog(
            prompt = unlockPrompt,
            discardsChanges = isDirty,
            onSubmit = onUnlock,
            onDismiss = onCancelUnlock,
        )
    }

    if (passwordPrompt != null) {
        DocumentPasswordDialog(
            mode = passwordPrompt,
            onSet = onSetPassword,
            onRemove = onRemovePassword,
            onUnlock = onStartUnlock,
            onDismiss = onCancelPasswordCommand,
        )
    }

    if (pageRewriteWarningShown) {
        AlertDialog(
            onDismissRequest = { onAnswerPageRewrite(false) },
            title = { Text(stringResource(R.string.page_rewrite_warning_title)) },
            text = { DialogBody { Text(stringResource(R.string.page_rewrite_warning)) } },
            confirmButton = {
                TextButton(onClick = { onAnswerPageRewrite(true) }) { Text(stringResource(R.string.action_continue)) }
            },
            dismissButton = {
                TextButton(onClick = { onAnswerPageRewrite(false) }) { Text(stringResource(R.string.cancel)) }
            },
        )
    }

    if (aboutOpen) {
        AboutDialog(
            onDismiss = { aboutOpen = false },
            onShowNotices = { aboutOpen = false; noticesOpen = true },
        )
    }

    pendingUnsavedAction?.let { action ->
        // Save, Discard or Cancel (#145, #378): Save proceeds once the document is saved;
        // Cancel keeps the document open with every change. Discard proceeds with the
        // document as it stood before this prompt — for Close that means closing without
        // writing the pending edits; for Share it means sharing the last-saved file while
        // those edits stay right where Cancel would have left them.
        AlertDialog(
            onDismissRequest = { pendingUnsavedAction = null },
            title = { Text(stringResource(R.string.unsaved_changes)) },
            text = { DialogBody { Text(stringResource(R.string.unsaved_changes_body)) } },
            confirmButton = {
                TextButton(onClick = {
                    pendingUnsavedAction = null
                    when (action) {
                        UnsavedAction.CLOSE -> onSaveAndClose()
                        UnsavedAction.SHARE -> onSaveAndShare()
                    }
                }) { Text(stringResource(R.string.save)) }
            },
            dismissButton = {
                Row {
                    TextButton(onClick = { pendingUnsavedAction = null }) { Text(stringResource(R.string.cancel)) }
                    TextButton(onClick = {
                        pendingUnsavedAction = null
                        when (action) {
                            UnsavedAction.CLOSE -> onClose()
                            UnsavedAction.SHARE -> onShareLastSaved()
                        }
                    }) { Text(stringResource(R.string.discard)) }
                }
            },
        )
    }

    Scaffold(
        topBar = {
            Column {
                if (searchOpen) {
                    SearchTopBar(
                        query = searchQuery,
                        hitCount = searchHits.size,
                        currentHitIndex = currentHitIndex,
                        isSearching = isSearching,
                        onQueryChange = onSearchQueryChange,
                        onPrevious = onSearchPrevious,
                        onNext = onSearchNext,
                        onClose = closeSearch,
                        screenshotMode = screenshotSheet == "search",
                    )
                } else {
                    TopAppBar(
                        title = {
                            Text(
                                (if (isDirty) "• " else "") + displayName,
                                maxLines = 1,
                                // One line, laid out as one line. With soft wrap on,
                                // Compose breaks the name at a space and then clips to
                                // the first line, so "Rental Agreement.pdf" showed
                                // "Rental" — and once the document was dirty, the break
                                // after the bullet left the title reading just "•".
                                softWrap = false,
                                overflow = TextOverflow.Ellipsis,
                            )
                        },
                        navigationIcon = {
                            IconButton(onClick = requestClose, enabled = !documentLocked) {
                                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close_document))
                            }
                        },
                        // #144: the top bar keeps the document's own commands — Save, and the
                        // overflow for everything done to the file as a whole. The editing tools
                        // live in the bottom bar, so the title keeps its width.
                        actions = {
                            // A mark deliberately leaves the document clean — nothing is
                            // written until the confirmation is answered — so gating Save on
                            // isDirty alone greyed it out with an area marked, and the
                            // confirmation onSave already raises was reachable only through
                            // the overflow's Save a copy. The Windows and Mac passes found
                            // exactly this; it was here too (#173).
                            val hasMarks = redactionMarks.values.any { it.isNotEmpty() }
                            val toolOn = stringResource(R.string.tool_on)
                            val toolOff = stringResource(R.string.tool_off)
                            TextButton(
                                onClick = onSave,
                                enabled = (isDirty || hasMarks) && !isSaving && !documentLocked,
                            ) {
                                Text(stringResource(if (isSaving) R.string.saving else R.string.save))
                            }
                            IconButton(onClick = { menuOpen = true }) {
                                Icon(Icons.Filled.MoreVert, contentDescription = stringResource(R.string.more_options))
                            }
                            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.save_a_copy)) },
                                    enabled = !isSaving && !documentLocked,
                                    onClick = { menuOpen = false; onSaveAs() },
                                )
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.share)) },
                                    enabled = !isSaving && !documentLocked,
                                    onClick = { menuOpen = false; requestShare() },
                                )
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.security_password_menu)) },
                                    enabled = hasDocumentFile && !isSaving && !documentLocked,
                                    onClick = { menuOpen = false; onStartPasswordCommand() },
                                )
                                if (capabilities.isRestricted) {
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.security_unlock_menu)) },
                                        enabled = hasDocumentFile && !isSaving && !documentLocked,
                                        onClick = { menuOpen = false; onStartUnlock() },
                                    )
                                }
                                HorizontalDivider()
                                // #328/#329: Redact is not an everyday tool — it removes
                                // content for good — and its icon means nothing to someone who
                                // has not been told what it is. In the menu it says its own
                                // name, and a screen reader reads a label rather than guessing
                                // at an icon. The armed state is a check mark, and a check
                                // mark is not something a screen reader can read, so it is also
                                // said as a state — the same pair the toolbar button carried.
                                //
                                // The state is said twice because it is read twice: the words
                                // ("Activé" / "Désactivé", so an unarmed tool states that it is
                                // off rather than saying nothing) and the `selected` trait,
                                // which is what a checkmarked menu row carries everywhere else,
                                // and which — unlike a state description — is an attribute the
                                // QA rig can read out of the accessibility dump.
                                val armedIcon: (@Composable () -> Unit)? = if (redactMode) {
                                    { Icon(Icons.Filled.Check, contentDescription = null) }
                                } else {
                                    null
                                }
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.redact)) },
                                    leadingIcon = armedIcon,
                                    modifier = Modifier.semantics {
                                        selected = redactMode
                                        stateDescription =
                                            if (redactMode) toolOn else toolOff
                                    },
                                    enabled = capabilities.canEditContent && !toolsDisabled,
                                    onClick = { menuOpen = false; onToggleRedact() },
                                )
                                // Clearing is one action and one undo step (#329), and it only
                                // exists while there is something to clear.
                                if (hasMarks) {
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.redact_clear_marks)) },
                                        enabled = capabilities.canEditContent && !toolsDisabled,
                                        onClick = { menuOpen = false; onClearRedactionMarks() },
                                    )
                                }
                                HorizontalDivider()
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.about_megapdf)) },
                                    onClick = { menuOpen = false; aboutOpen = true },
                                )
                            }
                        },
                    )
                }
                // #145: document-level work (opening, saving, searching) directly under the bar.
                if (busy != null) BusyStrip(busy.document)
            }
        },
        // #144: the everyday tools, one row at the bottom where a thumb reaches them
        // (Material 3 bottom app bar). Creating things on the left, history on the right.
        bottomBar = {
            BottomAppBar(
                actions = {
                    // A restricted document disables what its owner did not allow (#131),
                    // and a save or a slow change disables every editing tool (#145).
                    ToolbarAction(
                        icon = ToolbarIcons.Sign,
                        label = stringResource(R.string.sign),
                        enabled = capabilities.canSign && !toolsDisabled,
                        onClick = { signDialogOpen = true },
                    )
                    ToolbarAction(
                        icon = ToolbarIcons.AddText,
                        label = stringResource(R.string.add_text),
                        enabled = capabilities.canAddText && !toolsDisabled,
                        onClick = onStartTextPlacement,
                    )
                    // Redact is not here (#328). It moved into the ⋮ menu, where it says its
                    // own name: it is the one tool whose icon means nothing on its own, and
                    // a bar of five icons has no room to explain one of them.
                    ToolbarAction(
                        icon = Icons.Filled.Search,
                        label = stringResource(R.string.search),
                        onClick = { if (searchOpen) closeSearch() else searchOpen = true },
                    )
                    Spacer(Modifier.weight(1f))
                    ToolbarAction(
                        icon = ToolbarIcons.Undo,
                        label = stringResource(R.string.undo),
                        enabled = canUndo && !toolsDisabled,
                        onClick = onUndo,
                    )
                    ToolbarAction(
                        icon = ToolbarIcons.Redo,
                        label = stringResource(R.string.redo),
                        enabled = canRedo && !toolsDisabled,
                        onClick = onRedo,
                    )
                },
            )
        },
    ) { padding ->
        BoxWithConstraints(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .background(Brand.Backdrop)
                // Pinch zoom (#336). Only multi-touch is consumed, so single-finger
                // vertical scrolling still belongs to the LazyColumn — and that is
                // only true on the Initial pass. This box is the LazyColumn's
                // *parent*, and the Main pass reaches the child first: read there,
                // the event arrived after the list's scrollable had claimed the
                // touch slop and consumed the pointers. `calculateZoom` skips
                // consumed changes, so it was left working from one live pointer
                // and a centroid computed over two — the ratio it produced was
                // either 1 (nothing happened) or wrong (the page jumped), and
                // whether the slop was crossed in time decided which. The Initial
                // pass sees every pressed pointer before the list does, so a
                // two-finger pinch is claimed here and a one-finger drag is never
                // touched, which is what the comment always meant.
                .pointerInput(Unit) {
                    awaitEachGesture {
                        awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                        do {
                            val event = awaitPointerEvent(PointerEventPass.Initial)
                            if (event.changes.count { it.pressed } >= 2) {
                                val change = event.calculateZoom()
                                if (change != 1f) {
                                    zoom = (zoom * change).coerceIn(MIN_ZOOM, MAX_ZOOM)
                                    // Claim the gesture: two fingers moving apart
                                    // must not also scroll the list, and a pinch
                                    // already at the zoom limit must not turn into
                                    // a scroll either.
                                    event.changes.forEach { it.consume() }
                                }
                            }
                        } while (event.changes.any { it.pressed })
                    }
                },
        ) {
            val density = LocalDensity.current
            val containerWidthPx = with(density) { maxWidth.toPx() }
            val pageWidthDp = maxWidth * zoom

            // Re-render the visible ±2 window whenever scroll position or zoom
            // settles; debounce keeps pinch gestures from spamming the engine.
            LaunchedEffect(pageSizes) {
                snapshotFlow {
                    val info = listState.layoutInfo.visibleItemsInfo
                    Triple(
                        info.firstOrNull()?.index ?: 0,
                        info.lastOrNull()?.index ?: 0,
                        (containerWidthPx * zoom).toInt(),
                    )
                }
                    .distinctUntilChanged()
                    .debounce(200)
                    .collect { (first, last, widthPx) ->
                        onRenderWindowChange(first, last, widthPx)
                    }
            }

            // The current page — the one across the middle of the viewport — once scrolling
            // settles, so its page check can start before the first change on it (#145).
            LaunchedEffect(pageSizes) {
                snapshotFlow {
                    val info = listState.layoutInfo
                    val middle = (info.viewportStartOffset + info.viewportEndOffset) / 2
                    info.visibleItemsInfo.firstOrNull { it.offset <= middle && it.offset + it.size > middle }?.index
                        ?: info.visibleItemsInfo.firstOrNull()?.index
                }
                    .filterNotNull()
                    .distinctUntilChanged()
                    .debounce(300)
                    .collect { onCurrentPageChange(it) }
            }

            // Bring the current hit itself into view, not merely its page (#28).
            // Zoomed in, a page can be several screens tall and wider than the
            // display, so "the page is visible" says nothing about whether the match
            // is: scrolling only when the page was entirely off screen left hits
            // sitting below the fold, or off to the side, with the view never moving.
            LaunchedEffect(currentHitIndex, searchHits, zoom, pageSizes) {
                val hit = searchHits.getOrNull(currentHitIndex) ?: return@LaunchedEffect
                val page = pageSizes.getOrNull(hit.pageIndex) ?: return@LaunchedEffect
                val rect = hit.rects.firstOrNull() ?: return@LaunchedEffect

                val pageWidthPx = containerWidthPx * zoom
                val pageHeightPx = pageWidthPx * (page.heightPoints / page.widthPoints).toFloat()
                // Page points are bottom-left origin; the overlay flips them the same way.
                val hitTopPx = ((page.heightPoints - rect.top) / page.heightPoints).toFloat() * pageHeightPx
                val hitHeightPx = ((rect.top - rect.bottom) / page.heightPoints).toFloat() * pageHeightPx
                val viewportHeightPx = listState.layoutInfo.viewportSize.height.toFloat()
                val margin = viewportHeightPx * 0.15f

                // Scroll vertically unless the hit already sits comfortably on screen.
                val itemOffset = listState.layoutInfo.visibleItemsInfo
                    .firstOrNull { it.index == hit.pageIndex }?.offset?.toFloat()
                val hitY = itemOffset?.plus(hitTopPx)
                if (hitY == null || hitY < margin || hitY + hitHeightPx > viewportHeightPx - margin) {
                    listState.animateScrollToItem(
                        hit.pageIndex,
                        (hitTopPx - margin).toInt().coerceAtLeast(0),
                    )
                }

                // And horizontally, which nothing did before: when zoom > 1 the page is
                // wider than the display and the match can be entirely off to one side.
                if (zoom > 1f && hScroll.maxValue > 0) {
                    val hitCentreX = ((rect.left + rect.right) / 2.0 / page.widthPoints).toFloat() * pageWidthPx
                    val target = (hitCentreX - containerWidthPx / 2f)
                        .toInt()
                        .coerceIn(0, hScroll.maxValue)
                    if (kotlin.math.abs(target - hScroll.value) > containerWidthPx * 0.1f) {
                        hScroll.animateScrollTo(target)
                    }
                }
            }

            LazyColumn(
                state = listState,
                modifier = Modifier
                    .fillMaxSize()
                    .horizontalScroll(hScroll, enabled = zoom > 1f),
                horizontalAlignment = Alignment.CenterHorizontally,
                // Centred, not top-packed (#48): the Brand.Backdrop behind the list is
                // the document surround every PDF viewer draws so you can see where
                // the page ends. Top-packed and full-bleed, it could only ever show
                // below the last page — a third of the viewport in one dead block on
                // a one-page document. For a LazyColumn the arrangement applies only
                // while the content is shorter than the viewport, so a multi-page
                // document still packs from the top and scrolls unchanged.
                verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
            ) {
                items(pageSizes.size, key = { it }) { index ->
                    val size = pageSizes[index]
                    val bitmap = pageBitmaps[index]
                    // onTap defers ~300ms when onDoubleTap is present — that's the
                    // built-in disambiguation between check-a-box and zoom.
                    val pageModifier = Modifier
                        .width(pageWidthDp)
                        .aspectRatio((size.widthPoints / size.heightPoints).toFloat())
                        .background(Color.White)
                        // While Redact is armed a drag marks an area instead of scrolling
                        // (#173). The gesture is only installed when the tool is on, so
                        // the list keeps its scrolling the rest of the time.
                        .pointerInput(index, redactMode) {
                            if (!redactMode) return@pointerInput
                            var origin = androidx.compose.ui.geometry.Offset.Zero
                            var current = androidx.compose.ui.geometry.Offset.Zero
                            detectDragGestures(
                                onDragStart = { at ->
                                    origin = at
                                    current = at
                                    redactBand = RedactBand(index, at, at)
                                },
                                onDrag = { change, delta ->
                                    change.consume()
                                    current += delta
                                    redactBand = RedactBand(index, origin, current)
                                },
                                onDragCancel = { redactBand = null },
                                onDragEnd = {
                                    redactBand = null
                                    var left = minOf(origin.x, current.x) / this.size.width
                                    var right = maxOf(origin.x, current.x) / this.size.width
                                    var top = minOf(origin.y, current.y) / this.size.height
                                    var bottom = maxOf(origin.y, current.y) / this.size.height
                                    // A drag *along* a line is how text is redacted — the hint
                                    // says "drag across what you want removed, or select text"
                                    // — and such a drag is flat by nature. Both extents used to
                                    // have to clear the threshold, so a straight swipe along a
                                    // line produced no mark and said nothing: the band followed
                                    // the finger and then vanished. One extent is enough now, and
                                    // whichever one is thin is grown to cover what the drag was
                                    // drawn along; markTextForRedaction then snaps it to whole
                                    // glyphs. A tap has no extent either way and is still not a
                                    // mark (#173).
                                    val wide = right - left > MIN_MARK_EXTENT
                                    val tall = bottom - top > MIN_MARK_EXTENT
                                    if (wide || tall) {
                                        if (bottom - top < MIN_MARK_THICKNESS) {
                                            val middle = (top + bottom) / 2f
                                            top = middle - MIN_MARK_THICKNESS / 2
                                            bottom = middle + MIN_MARK_THICKNESS / 2
                                        }
                                        if (right - left < MIN_MARK_THICKNESS) {
                                            val middle = (left + right) / 2f
                                            left = middle - MIN_MARK_THICKNESS / 2
                                            right = middle + MIN_MARK_THICKNESS / 2
                                        }
                                        onMarkForRedaction(
                                            index,
                                            com.megapdf.engine.PdfRect(
                                                left * size.widthPoints,
                                                (1.0 - bottom) * size.heightPoints,
                                                right * size.widthPoints,
                                                (1.0 - top) * size.heightPoints,
                                            ),
                                        )
                                    }
                                },
                            )
                        }
                        .pointerInput(index) {
                            detectTapGestures(
                                onTap = { offset ->
                                    onPageTap(
                                        index,
                                        offset.x / this.size.width,
                                        offset.y / this.size.height,
                                    )
                                },
                                onDoubleTap = { zoom = if (zoom < 1.5f) 2f else 1f },
                            )
                        }
                    androidx.compose.foundation.layout.Box(pageModifier) {
                        if (bitmap != null) {
                            Image(
                                bitmap = bitmap.asImageBitmap(),
                                contentDescription = stringResource(R.string.page_n, index + 1),
                                modifier = Modifier.fillMaxSize(),
                                contentScale = ContentScale.Fit,
                            )
                        }
                        val marks = redactionMarks[index]
                        if (!marks.isNullOrEmpty()) {
                            RedactionMarkOverlay(
                                marks = marks,
                                pageSize = size,
                                // While Redact is armed a drag across the page marks a new
                                // area, so the existing marks step out of the way rather
                                // than fight that gesture for the touch.
                                selectable = !redactMode
                                    && capabilities.canEditContent
                                    && !toolsDisabled,
                                onSelect = { onSelectRedactionMark(index, it) },
                                onRemove = { onRemoveRedactionMark(index, it) },
                            )
                        }
                        redactBand?.let { band ->
                            if (band.pageIndex == index) RedactionBandOverlay(band)
                        }
                        // The chrome for the selected mark (#329): the same box a selected
                        // stamp or text box gets, minus the aspect lock — a redaction area is
                        // a rectangle by nature, so its corner grip resizes each side freely.
                        if (selectedRedactionMark != null && selectedRedactionMark.pageIndex == index) {
                            SelectionOverlay(
                                key = selectedRedactionMark,
                                rect = selectedRedactionMark.rect,
                                pageSize = size,
                                aspectLocked = false,
                                onCommit = { onCommitRedactionMarkRect(index, selectedRedactionMark.markId, it) },
                                onRemove = { onRemoveRedactionMark(index, selectedRedactionMark.markId) },
                                enabled = !editingBlocked,
                            )
                        }
                        val pageHits = searchHits.withIndex()
                            .filter { it.value.pageIndex == index }
                        if (pageHits.isNotEmpty()) {
                            SearchHighlightOverlay(
                                hits = pageHits,
                                currentHitIndex = currentHitIndex,
                                pageSize = size,
                            )
                        }
                        if (selectedStamp != null && selectedStamp.pageIndex == index) {
                            SelectionOverlay(
                                key = selectedStamp,
                                rect = selectedStamp.rect,
                                pageSize = size,
                                onCommit = onCommitStampRect,
                                onRemove = onRemoveStamp,
                                enabled = !editingBlocked,
                            )
                        }
                        if (selectedTextBox != null && selectedTextBox.pageIndex == index) {
                            SelectionOverlay(
                                key = selectedTextBox,
                                rect = selectedTextBox.rect,
                                pageSize = size,
                                // No resize handle: resizing text means changing its
                                // font size, and SDD §3.1 keeps formatting out (#36).
                                resizable = false,
                                onCommit = onCommitTextBoxRect,
                                onRemove = onRemoveTextBox,
                                onEdit = onEditTextBox,
                                enabled = !editingBlocked,
                            )
                        }
                        val pageBusy = busy?.page
                        val spot = pageBusy?.spot
                        if (pageBusy != null && pageBusy.isVisible && spot != null && spot.pageIndex == index) {
                            PageBusySpinner(spot = spot, label = pageBusy.label, pageSize = size)
                        }
                    }
                }
            }
        }
    }

    // A full-screen overlay with its own Scaffold, drawn over the viewer (as on Home).
    if (noticesOpen) {
        ThirdPartyNoticesScreen(onClose = { noticesOpen = false })
    }
}

/**
 * The document-level busy strip (#145): an indeterminate bar and what is happening, directly
 * under the top app bar. It appears only after half a second, and its label is a polite live
 * region so TalkBack reads it without taking focus.
 */
@Composable
private fun BusyStrip(indicator: BusyIndicator) {
    val label = indicator.label
    if (!indicator.isVisible || label == null) return
    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface),
    ) {
        LinearProgressIndicator(Modifier.fillMaxWidth())
        Text(
            stringResource(label.stringId),
            style = MaterialTheme.typography.labelMedium,
            modifier = Modifier
                .padding(horizontal = 16.dp, vertical = 4.dp)
                .semantics { liveRegion = LiveRegionMode.Polite },
        )
    }
}

/**
 * The page-level busy spinner (#145): just past the end of the line or box being worked on, or
 * mid-page when the work has no place of its own. Page points are bottom-left origin, flipped
 * the same way as [SelectionOverlay].
 */
@Composable
private fun PageBusySpinner(spot: BusySpot, label: BusyLabel?, pageSize: PageSize) {
    val description = label?.let { stringResource(it.stringId) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val widthPx = constraints.maxWidth.toFloat()
        val heightPx = constraints.maxHeight.toFloat()
        val sx = widthPx / pageSize.widthPoints.toFloat()
        val sy = heightPx / pageSize.heightPoints.toFloat()
        val sizePx = with(density) { 32.dp.toPx() }
        val gapPx = with(density) { 8.dp.toPx() }
        val rect = spot.rect
        val centreX = if (rect == null) widthPx / 2 else rect.right.toFloat() * sx + gapPx + sizePx / 2
        val centreY = if (rect == null) heightPx / 2
        else (pageSize.heightPoints - (rect.top + rect.bottom) / 2).toFloat() * sy
        val left = (centreX - sizePx / 2).coerceIn(0f, (widthPx - sizePx).coerceAtLeast(0f))
        val top = (centreY - sizePx / 2).coerceIn(0f, (heightPx - sizePx).coerceAtLeast(0f))
        Surface(
            shape = CircleShape,
            shadowElevation = 2.dp,
            modifier = Modifier
                .offset { androidx.compose.ui.unit.IntOffset(left.roundToInt(), top.roundToInt()) }
                .size(32.dp)
                .semantics {
                    if (description != null) contentDescription = description
                    liveRegion = LiveRegionMode.Polite
                },
        ) {
            // 2.dp is a stroke width, not layout spacing (docs/design-tokens.md §3).
            CircularProgressIndicator(Modifier.padding(8.dp), strokeWidth = 2.dp)
        }
    }
}

/**
 * One bottom-bar tool (#144): an icon button whose label is its content description
 * for TalkBack and, on a long press, a plain tooltip for everyone else — a phone's
 * bar has no room for text under five icons.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ToolbarAction(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
    /** A mode's on/off state, or null for a button that does something once. */
    armed: Boolean? = null,
) {
    // A tool that can be armed says which it is, in words as well as in ink (#173).
    // The fill below is the whole visual difference, and it reached the accessibility
    // tree as nothing at all: armed and not armed produced byte-identical nodes, so a
    // screen reader was told "Redact" either way — the same gap the Mac and iOS passes
    // found, and the one this issue is open for. stateDescription is what TalkBack
    // reads out after the label, and it is set here rather than left to `selected`
    // because "on"/"off" is what a tool is, where "selected" is what a list row is.
    val on = stringResource(R.string.tool_on)
    val off = stringResource(R.string.tool_off)
    val state = if (armed == null) Modifier else Modifier.semantics {
        selected = armed
        stateDescription = if (armed) on else off
    }
    TooltipBox(
        positionProvider = TooltipDefaults.rememberPlainTooltipPositionProvider(),
        tooltip = { PlainTooltip { Text(label) } },
        state = rememberTooltipState(),
    ) {
        // An armed tool says so: a mode with no visible affordance is a mode people get
        // stuck in (SDD §2.2). FilledIconButton is Material 3's "this is on".
        if (armed == true) {
            androidx.compose.material3.FilledIconButton(
                onClick = onClick, enabled = enabled, modifier = state,
            ) {
                Icon(icon, contentDescription = label)
            }
        } else {
            IconButton(onClick = onClick, enabled = enabled, modifier = state) {
                Icon(icon, contentDescription = label)
            }
        }
    }
}

/**
 * Search-mode top bar (#26): as-you-type query field, "N of M" match count
 * ("No results" when the sweep comes back empty), previous/next chevrons
 * (wrapping around document ends), and close, which clears all highlights.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SearchTopBar(
    query: String,
    hitCount: Int,
    currentHitIndex: Int,
    isSearching: Boolean,
    onQueryChange: (String) -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onClose: () -> Unit,
    screenshotMode: Boolean = false,
) {
    val focusRequester = remember { FocusRequester() }
    TopAppBar(
        title = {
            TextField(
                value = query,
                onValueChange = onQueryChange,
                placeholder = { Text(stringResource(R.string.search)) },
                singleLine = true,
                modifier = Modifier
                    .fillMaxWidth()
                    .focusRequester(focusRequester),
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = Color.Transparent,
                    unfocusedContainerColor = Color.Transparent,
                ),
            )
        },
        navigationIcon = {
            IconButton(onClick = onClose) {
                Icon(Icons.Filled.Close, contentDescription = stringResource(R.string.close_search))
            }
        },
        actions = {
            Text(
                when {
                    query.isEmpty() || isSearching -> ""
                    hitCount == 0 -> stringResource(R.string.no_results)
                    else -> stringResource(R.string.match_counter, currentHitIndex + 1, hitCount)
                },
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                // The top bar measures its actions first and gives the title what is
                // left, so an unbounded counter takes the query field's room: at the
                // largest text size "Aucun résultat" left a field a few pixels wide
                // with the typed word invisible (#146). Capped, it is the counter that
                // shortens and the field that survives.
                modifier = Modifier.widthIn(max = 112.dp),
            )
            IconButton(onClick = onPrevious, enabled = hitCount > 0) {
                Icon(Icons.Filled.KeyboardArrowUp, contentDescription = stringResource(R.string.previous_match))
            }
            IconButton(onClick = onNext, enabled = hitCount > 0) {
                Icon(Icons.Filled.KeyboardArrowDown, contentDescription = stringResource(R.string.next_match))
            }
        },
    )
    // Screenshot mode skips the focus grab: whether the soft keyboard would
    // then cover the page depends on the emulator's hw.keyboard setting, and a
    // marketing capture may not depend on that.
    LaunchedEffect(Unit) { if (!screenshotMode) focusRequester.requestFocus() }
}

/**
 * Translucent fills over every search hit on this page; the current hit gets
 * the distinct color. Hit rects are PDF points (bottom-left origin) mapped
 * into the page box's pixel space, same transform as [SelectionOverlay].
 */
@Composable
private fun SearchHighlightOverlay(
    hits: List<IndexedValue<SearchHit>>,
    currentHitIndex: Int,
    pageSize: PageSize,
) {
    androidx.compose.foundation.Canvas(Modifier.fillMaxSize()) {
        val sx = size.width / pageSize.widthPoints.toFloat()
        val sy = size.height / pageSize.heightPoints.toFloat()
        for ((hitIndex, hit) in hits) {
            val color =
                if (hitIndex == currentHitIndex) CURRENT_MATCH_HIGHLIGHT else MATCH_HIGHLIGHT
            for (rect in hit.rects) {
                drawRect(
                    color = color,
                    topLeft = androidx.compose.ui.geometry.Offset(
                        (rect.left * sx).toFloat(),
                        ((pageSize.heightPoints - rect.top) * sy).toFloat(),
                    ),
                    size = androidx.compose.ui.geometry.Size(
                        ((rect.right - rect.left) * sx).toFloat(),
                        ((rect.top - rect.bottom) * sy).toFloat(),
                    ),
                )
            }
        }
    }
}

/** The rubber band while a redaction drag is in progress, in page-box pixels. */
data class RedactBand(
    val pageIndex: Int,
    val origin: androidx.compose.ui.geometry.Offset,
    val current: androidx.compose.ui.geometry.Offset,
)

/**
 * Areas marked for redaction, drawn over the page (#173). Translucent with an outline, so
 * the user can still read what they are about to remove — which is the reason marking and
 * applying are two steps. Over the page rather than into it because a mark is never written
 * to the file: there is nothing in the raster to draw, and marking costs no re-render.
 */
@Composable
private fun RedactionMarkOverlay(
    marks: List<com.megapdf.engine.RedactionMark>,
    pageSize: PageSize,
    selectable: Boolean,
    onSelect: (Int) -> Unit,
    onRemove: (Int) -> Unit,
) {
    // The marks had no accessible presence at all: a Canvas draws pixels, and a Canvas
    // publishes no node, so a screen reader could hear "Marked for redaction." once and then
    // find nothing on the page (#173, the same fault as the Mac's). Each mark is now a node
    // of its own (#329), because a mark can be tapped and removed, and removal has to be
    // reachable by a screen reader as well as by a finger. The count stays on the container,
    // so "2 areas marked for redaction" — the wording the save confirmation uses — is still
    // what is heard before saving.
    val description = if (marks.size == 1) stringResource(R.string.redact_mark_count_one)
                      else stringResource(R.string.redact_mark_count, marks.size)
    val markName = stringResource(R.string.redact_mark_name)
    val removeLabel = stringResource(R.string.redact_mark_remove)
    val density = LocalDensity.current
    BoxWithConstraints(Modifier.fillMaxSize().semantics { contentDescription = description }) {
        val sx = constraints.maxWidth.toFloat() / pageSize.widthPoints.toFloat()
        val sy = constraints.maxHeight.toFloat() / pageSize.heightPoints.toFloat()
        for (mark in marks) {
            val left = (mark.rect.left * sx).toFloat()
            val top = ((pageSize.heightPoints - mark.rect.top) * sy).toFloat()
            val width = ((mark.rect.right - mark.rect.left) * sx).toFloat()
            val height = ((mark.rect.top - mark.rect.bottom) * sy).toFloat()
            androidx.compose.foundation.layout.Box(
                Modifier
                    .offset { androidx.compose.ui.unit.IntOffset(left.roundToInt(), top.roundToInt()) }
                    .size(
                        with(density) { width.toDp() },
                        with(density) { height.toDp() },
                    )
                    .background(REDACTION_MARK)
                    // 2.dp is a stroke width, not layout spacing (docs/design-tokens.md §3).
                    .border(2.dp, REDACTION_MARK_OUTLINE)
                    .then(
                        if (!selectable) Modifier else Modifier
                            .clickable { onSelect(mark.markId) }
                            .semantics {
                                contentDescription = markName
                                // The mark is already selected when its chrome is on it;
                                // this is for the rest of them.
                                customActions = listOf(
                                    CustomAccessibilityAction(removeLabel) {
                                        onRemove(mark.markId)
                                        true
                                    },
                                )
                            }
                    )
            )
        }
    }
}

/** The band a redaction drag is drawing, in the same ink as the marks it will become. */
@Composable
private fun RedactionBandOverlay(band: RedactBand) {
    androidx.compose.foundation.Canvas(Modifier.fillMaxSize()) {
        val topLeft = androidx.compose.ui.geometry.Offset(
            minOf(band.origin.x, band.current.x),
            minOf(band.origin.y, band.current.y),
        )
        val boxSize = androidx.compose.ui.geometry.Size(
            kotlin.math.abs(band.current.x - band.origin.x),
            kotlin.math.abs(band.current.y - band.origin.y),
        )
        drawRect(color = REDACTION_MARK, topLeft = topLeft, size = boxSize)
        drawRect(
            color = REDACTION_MARK_OUTLINE,
            topLeft = topLeft,
            size = boxSize,
            style = androidx.compose.ui.graphics.drawscope.Stroke(width = 2f),
        )
    }
}

/**
 * What a base-14 face is called in the UI. The PDF names are exact and must not
 * change (SDD §6.2 contract 4); these are only what the chips say.
 */
private fun fontLabel(fontName: String): String = when (fontName) {
    "Times-Roman" -> "Times"
    else -> fontName
}

/**
 * One labelled row of single-choice chips — the size and face pickers (#43).
 * Horizontally scrollable so a narrow phone never clips the last option.
 */
@Composable
private fun <T> ChipRow(
    label: String,
    options: List<T>,
    selected: T,
    labelOf: (T) -> String,
    onSelect: (T) -> Unit,
) {
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Row(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.horizontalScroll(rememberScrollState()),
        ) {
            options.forEach { option ->
                FilterChip(
                    selected = option == selected,
                    onClick = { onSelect(option) },
                    label = { Text(labelOf(option)) },
                )
            }
        }
    }
}

/**
 * Selection chrome for something the user placed on the page: drag to move,
 * corner handle to resize (aspect locked), X to remove. Changes commit to the
 * engine on drag end; the page bitmap refreshes after each commit.
 *
 * Signatures and text boxes share it (#36) rather than growing a second
 * interaction model — [resizable] and [onEdit] are the only differences between
 * them. [key] is whatever identifies the current selection; the in-progress drag
 * resets whenever it changes. While [enabled] is false (a change is still going in,
 * #145) it neither drags nor takes taps, and a finished drag stays where it was dropped.
 */
@Composable
private fun SelectionOverlay(
    key: Any,
    rect: com.megapdf.engine.PdfRect,
    pageSize: PageSize,
    onCommit: (com.megapdf.engine.PdfRect) -> Unit,
    onRemove: () -> Unit,
    resizable: Boolean = true,
    onEdit: (() -> Unit)? = null,
    enabled: Boolean = true,
    /**
     * A signature keeps its shape when it is resized; a redaction area is a rectangle by
     * nature and its corner grip moves each side on its own (#329).
     */
    aspectLocked: Boolean = true,
) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val pxWidth = constraints.maxWidth.toFloat()
        val pxHeight = constraints.maxHeight.toFloat()
        val sx = pxWidth / pageSize.widthPoints.toFloat()
        val sy = pxHeight / pageSize.heightPoints.toFloat()

        var drag by remember(key) { mutableStateOf(androidx.compose.ui.geometry.Offset.Zero) }
        var widthDelta by remember(key) { mutableFloatStateOf(0f) }
        var heightDelta by remember(key) { mutableFloatStateOf(0f) }

        val baseX = (rect.left * sx).toFloat()
        val baseY = ((pageSize.heightPoints - rect.top) * sy).toFloat()
        val baseW = ((rect.right - rect.left) * sx).toFloat()
        val baseH = ((rect.top - rect.bottom) * sy).toFloat()
        // The deltas only ever move when the resize handle exists, so a non-resizable
        // selection commits at scale 1 — a pure translation.
        val scaleX = ((baseW + widthDelta) / baseW).coerceAtLeast(0.15f)
        val scaleY = if (aspectLocked) scaleX else ((baseH + heightDelta) / baseH).coerceAtLeast(0.15f)

        fun commit() {
            val dxPt = drag.x / sx
            val dyPt = drag.y / sy
            val newLeft = rect.left + dxPt
            val newTop = rect.top - dyPt
            val newW = (rect.right - rect.left) * scaleX
            val newH = (rect.top - rect.bottom) * scaleY
            onCommit(com.megapdf.engine.PdfRect(newLeft, newTop - newH, newLeft + newW, newTop))
        }

        androidx.compose.foundation.layout.Box(
            Modifier
                .offset {
                    androidx.compose.ui.unit.IntOffset(
                        (baseX + drag.x).roundToInt(),
                        (baseY + drag.y).roundToInt(),
                    )
                }
                .size(
                    with(density) { (baseW * scaleX).toDp() },
                    with(density) { (baseH * scaleY).toDp() },
                )
                // 2.dp is a stroke width and 6/2 below is badge padding sized to
                // its glyph — neither is layout spacing, so docs/design-tokens.md
                // §3's grid does not apply to them.
                .border(2.dp, Brand.Accent)
                .pointerInput(key, enabled) {
                    if (!enabled) return@pointerInput
                    detectDragGestures(
                        onDrag = { change, delta -> change.consume(); drag += delta },
                        onDragEnd = { commit() },
                    )
                },
        ) {
            Text(
                "✕",
                color = Color.White,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .background(Brand.Danger)
                    .padding(horizontal = 6.dp, vertical = 2.dp)
                    .clickable(enabled = enabled) { onRemove() },
            )
            if (onEdit != null) {
                Text(
                    "✎",
                    color = Color.White,
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .background(Brand.Accent)
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                        .clickable(enabled = enabled) { onEdit() },
                )
            }
            if (resizable) {
                androidx.compose.foundation.layout.Box(
                    Modifier
                        .align(Alignment.BottomEnd)
                        // 18.dp: a resize grip, sized to be grabbable without
                        // covering what it resizes. Rounding it to the grid would
                        // shrink an already-small touch target.
                        .size(18.dp)
                        .background(Brand.Accent)
                        .pointerInput(key, enabled) {
                            if (!enabled) return@pointerInput
                            detectDragGestures(
                                onDrag = { change, delta ->
                                    change.consume()
                                    widthDelta += delta.x
                                    if (!aspectLocked) heightDelta += delta.y
                                },
                                onDragEnd = { commit() },
                            )
                        },
                )
            }
        }
    }
}
