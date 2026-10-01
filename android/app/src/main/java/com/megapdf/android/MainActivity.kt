package com.megapdf.android

import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.enableEdgeToEdge
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import com.megapdf.android.ui.MegaPdfTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.res.stringResource
import kotlinx.coroutines.launch
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.viewmodel.compose.viewModel

class MainActivity : ComponentActivity() {
    // Held here, not just inside the composable's default `viewModel()`, so onCreate and
    // onNewIntent (#376) share the same instance the UI observes rather than each resolving
    // their own.
    private val viewModel: ViewerViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        // Edge-to-edge, declared rather than inherited (#40). Targeting API 36
        // makes it mandatory — Android 16 ignores the opt-out — so saying it here
        // means every OS version behaves the same way instead of only the new ones.
        //
        // Both bars are forced to the *light* style: MegaPDF has no dark theme
        // (MegaPdfTheme applies the light brand scheme regardless of the system
        // setting), so the automatic style would paint white icons onto a white
        // app whenever the device is in dark mode. ui/Brand.kt carries a dark
        // scheme that is deliberately not wired up — turning it on means
        // revisiting this, not just swapping the argument.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        val screenshotState = intent.getStringExtra("screenshot")
        handleViewIntent(intent)
        setContent {
            MegaPdfTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    MegaPdfApp(viewModel = viewModel, screenshotState = screenshotState)
                }
            }
        }
    }

    /**
     * A second `ACTION_VIEW` (#376) while this activity is already running — the same PDF
     * viewer, launched again from another app's chooser rather than a fresh process. Single
     * activity, so there is no second window to open it in: it replaces whatever is on
     * screen, same as picking a different document from the in-app Open would, guarded by
     * the same unsaved-changes prompt.
     */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleViewIntent(intent)
    }

    private fun handleViewIntent(intent: Intent) {
        ViewIntent.uriToOpen(intent.action, intent.data)?.let(viewModel::requestOpenExternal)
    }
}

/**
 * The name Save a copy offers after a redaction (#173): "lease.pdf" becomes
 * "lease-redacted.pdf". The suffix is localised and carries no accent, because it is a
 * file name.
 */
private fun redactedName(displayName: String): String {
    val dot = displayName.lastIndexOf('.')
    val stem = if (dot > 0) displayName.substring(0, dot) else displayName
    val extension = if (dot > 0) displayName.substring(dot) else ".pdf"
    return stem + REDACTED_SUFFIX + extension
}

/** Kept here rather than read from resources: this runs outside a composable. */
private const val REDACTED_SUFFIX = "-redacted"

/**
 * The name *Save pages as…* offers (#174): "lease.pdf" becomes "lease-pages.pdf", so the copy is
 * never offered under the name of the document it came out of. Like [redactedName], the suffix is
 * a file name and carries no accent.
 */
private fun extractName(displayName: String): String {
    val dot = displayName.lastIndexOf('.')
    val stem = if (dot > 0) displayName.substring(0, dot) else displayName
    val extension = if (dot > 0) displayName.substring(dot) else ".pdf"
    return stem + PAGES_SUFFIX + extension
}

private const val PAGES_SUFFIX = "-pages"

/**
 * What the Redact confirmation (#173) was opened for: marks are on the document and one of
 * the commands that reads it has been asked for, so the marked content is removed first.
 * [SAVE] is either save path (Save, Save a copy); [EXPORT] is Export as Markdown (#409, as on
 * iOS): an export reads the document's text, so an unapplied mark would leak straight into
 * the `.md` file if the question were skipped.
 */
private enum class RedactConfirm { SAVE, EXPORT }

@Composable
fun MegaPdfApp(viewModel: ViewerViewModel = viewModel(), screenshotState: String? = null) {
    LaunchedEffect(screenshotState) { viewModel.applyScreenshotMode(screenshotState) }
    val context = LocalContext.current
    val openDocument = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri -> uri?.let { viewModel.openUri(it) } }
    // "Save a copy" is a PDF, and only a PDF (#409). What the row means on Android, and has
    // meant since before #386: the person picks a place and a name, the document is written
    // there as a PDF, and that copy becomes the current document — the title changes to the
    // new name, its grant is persisted and it goes into Recents ([ViewerViewModel.saveAs]).
    // That is the desktops' Save As, and the title changing is how it shows.
    //
    // The *type* of the picker decides what is written, never the *name* the provider hands
    // back. #386 had offered Markdown as a second entry in this same picker's mime-type list
    // and read the picked extension back to choose a binding; DocumentsUI ignores
    // `EXTRA_MIME_TYPES` for ACTION_CREATE_DOCUMENT, so a person who typed `lease.md` got
    // `lease.md.pdf` — a real PDF, silently made the current document — and no `.md` could be
    // produced at all (#409). There is no name-based decision any more: this launcher is
    // typed application/pdf and always saves a PDF copy; the Markdown export has its own row,
    // its own launcher ([exportMarkdown]) and its own write path, and no picker carries
    // `EXTRA_MIME_TYPES`.
    val createDocument = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/pdf")
    ) { uri -> uri?.let { viewModel.saveAs(it) } }
    // "Export as Markdown" (#386, #409): a one-way text export, never the app's current
    // document — see [ViewerViewModel.exportMarkdown]'s own note — through a picker typed
    // text/markdown with a `.md` suggested name ([markdownExportName]). Whatever the provider
    // names the file, it is written as Markdown: the row was the choice.
    val exportMarkdown = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("text/markdown")
    ) { uri -> uri?.let { viewModel.exportMarkdown(it) } }
    val pickSignatureImage = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia()
    ) { uri -> uri?.let { viewModel.importSignature(it) } }
    // Page tools (#174). "Add pages from file…" picks a PDF to take pages out of; the insertion
    // point is where the Pages grid's selection says, read when the picker comes back. "Save pages
    // as…" is a create-document picker of its own rather than a second face of Save a copy, for
    // #409's reason: the row is the choice, and the picker's type is what gets written.
    val pickPagesSource = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri -> uri?.let { viewModel.importPagesFrom(it) } }
    val createExtract = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/pdf")
    ) { uri -> uri?.let { viewModel.extractSelectedPagesTo(it) } }

    // Settings (#513): a full-screen overlay over whatever is underneath, reachable from the
    // viewer's ⋮ menu and from Home — *Open documents in reading mode* has to be settable
    // before a document is open, not only while one is.
    var settingsOpen by androidx.compose.runtime.remember {
        androidx.compose.runtime.mutableStateOf(false)
    }

    // The Redact confirmation is open (#173): marks are on the document and a save — or an
    // export (#409) — has been asked for, so the question comes before anything is written.
    var redactConfirm by androidx.compose.runtime.remember {
        androidx.compose.runtime.mutableStateOf<RedactConfirm?>(null)
    }
    val scope = androidx.compose.runtime.rememberCoroutineScope()

    // Status toasts ("Saved", save errors): one per message, keyed on the view model's own
    // count of them rather than on the text (#611). Keying on the text meant showing a toast
    // had to erase the message to avoid showing it twice — and the message is also the only
    // record of what the app said, so erasing it left nothing for anything else to read. The
    // count does the de-duplication instead, and the same message twice is still two toasts.
    //
    // The last serial already said is remembered across an activity recreation, because the view
    // model outlives one and a fresh composition would otherwise toast the last message again on
    // every rotation — which erasing the message used to prevent for free.
    var saidSerial by androidx.compose.runtime.saveable.rememberSaveable {
        androidx.compose.runtime.mutableStateOf(0)
    }
    val statusSerial = viewModel.statusSerial
    LaunchedEffect(statusSerial) {
        if (statusSerial <= saidSerial) return@LaunchedEffect
        saidSerial = statusSerial
        viewModel.statusMessage?.let { Toast.makeText(context, it, Toast.LENGTH_SHORT).show() }
    }

    // Share (#378): once the view model has a copy ready under cacheDir/share/, hand it to
    // the OS chooser through the FileProvider's content:// grant, then clear the request so
    // rotation or recomposition doesn't reopen the chooser.
    val shareFile = viewModel.shareFile
    LaunchedEffect(shareFile) {
        if (shareFile != null) {
            val uri = androidx.core.content.FileProvider.getUriForFile(
                context, "${context.packageName}.fileprovider", shareFile,
            )
            val sendIntent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
                type = "application/pdf"
                putExtra(android.content.Intent.EXTRA_STREAM, uri)
                addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startActivity(android.content.Intent.createChooser(sendIntent, null))
            viewModel.consumeShareFile()
        }
    }

    if (settingsOpen) {
        SettingsScreen(
            pageTint = viewModel.pageTint,
            openInReadingMode = viewModel.openInReadingMode,
            onPageTintChange = viewModel::choosePageColours,
            onOpenInReadingModeChange = viewModel::chooseOpenInReadingMode,
            onClose = { settingsOpen = false },
        )
    // Nothing underneath is composed while Settings is up: it is a screen, not a sheet, and
    // leaving the viewer in the tree behind it would leave its bars and its page in the
    // accessibility tree too.
    } else when (val state = viewModel.uiState) {
        is ViewerUiState.Home -> HomeScreen(
            recents = state.recents,
            error = state.error,
            onOpenClick = { openDocument.launch(arrayOf("application/pdf")) },
            onRecentClick = viewModel::openRecent,
            onRemoveRecent = viewModel::removeRecent,
            onSettingsClick = { settingsOpen = true },
        )

        is ViewerUiState.Loading -> LoadingScreen(viewModel.busy.document)

        is ViewerUiState.PasswordNeeded -> PasswordDialog(
            wrongPassword = state.wrongPassword,
            onSubmit = { password -> viewModel.openUri(state.uri, password) },
            onDismiss = viewModel::closeDocument,
        )

        is ViewerUiState.Viewing -> {
            // The Pages grid (#174) replaces the viewer while it is up rather than sitting over it
            // — the choice Settings makes, for its reason: a phone has no room for both, and
            // leaving the viewer composed underneath would leave its bars and its page in the
            // accessibility tree. Back returns to the document on the page it was left on.
            if (viewModel.isPagesOpen) PagesScreen(
                pageSizes = state.pageSizes,
                thumbnails = viewModel.pageThumbnails,
                selection = viewModel.selectedPages,
                canUndo = viewModel.canUndo,
                canRedo = viewModel.canRedo,
                busy = viewModel.busy,
                toolsDisabled = viewModel.toolsDisabled,
                pageTint = viewModel.pageTint,
                onThumbnailWindowChange = viewModel::updateThumbnailWindow,
                onToggleSelection = viewModel::togglePageSelection,
                onSelectAll = viewModel::selectAllPages,
                onClearSelection = viewModel::clearPageSelection,
                onRotate = viewModel::rotateSelectedPages,
                onDelete = viewModel::deleteSelectedPages,
                onMove = viewModel::movePage,
                onInsertBlank = viewModel::insertBlankPage,
                onAddFromFile = { pickPagesSource.launch(arrayOf("application/pdf")) },
                onSaveSelectionAs = { createExtract.launch(extractName(state.displayName)) },
                onUndo = viewModel::undo,
                onRedo = viewModel::redo,
                onClose = viewModel::closePages,
            ) else ViewerScreen(
                displayName = state.displayName,
                pageSizes = state.pageSizes,
                pageBitmaps = viewModel.pageBitmaps,
                isDirty = viewModel.isDirty,
                isSaving = viewModel.isSaving,
                signatures = viewModel.signatures,
                selectedStamp = viewModel.selectedStamp,
                selectedTextBox = viewModel.selectedTextBox,
                canUndo = viewModel.canUndo,
                canRedo = viewModel.canRedo,
                pendingTextTap = viewModel.pendingTextTap,
                pendingBodyEdit = viewModel.pendingBodyEdit,
                notice = viewModel.notice,
                searchQuery = viewModel.searchQuery,
                searchHits = viewModel.searchHits,
                currentHitIndex = viewModel.currentHitIndex,
                isSearching = viewModel.isSearching,
                onRenderWindowChange = viewModel::updateRenderWindow,
                onPageTap = viewModel::onPageTapped,
                onSearchQueryChange = viewModel::updateSearchQuery,
                onSearchPrevious = viewModel::previousSearchHit,
                onSearchNext = viewModel::nextSearchHit,
                onCloseSearch = viewModel::closeSearch,
                onStartPlacement = viewModel::startPlacement,
                onAddSignature = {
                    pickSignatureImage.launch(
                        androidx.activity.result.PickVisualMediaRequest(
                            ActivityResultContracts.PickVisualMedia.ImageOnly
                        )
                    )
                },
                onSaveDrawnSignature = viewModel::addDrawnSignature,
                onDeleteSignature = viewModel::deleteSignature,
                onRenameSignature = viewModel::renameSignature,
                loadSignatureBitmap = viewModel::loadSignatureBitmap,
                screenshotSheet = viewModel.screenshotSheet,
                onUndo = viewModel::undo,
                onRedo = viewModel::redo,
                onStartTextPlacement = viewModel::startTextPlacement,
                onCommitText = viewModel::commitText,
                onCancelTextPlacement = viewModel::cancelTextPlacement,
                onCommitBodyEdit = viewModel::commitBodyEdit,
                onCancelBodyEdit = viewModel::cancelBodyEdit,
                onCommitStampRect = viewModel::commitStampRect,
                onRemoveStamp = viewModel::removeSelectedStamp,
                onCommitTextBoxRect = viewModel::commitTextBoxRect,
                onEditTextBox = viewModel::editSelectedTextBox,
                onRemoveTextBox = viewModel::removeSelectedTextBox,
                onSave = {
                    if (viewModel.redactionMarkCount > 0) redactConfirm = RedactConfirm.SAVE else viewModel.save()
                },
                onSaveAs = {
                    if (viewModel.redactionMarkCount > 0) {
                        redactConfirm = RedactConfirm.SAVE
                    } else {
                        createDocument.launch(state.displayName)
                    }
                },
                // No unsaved-changes question here, unlike Share (#378): Share sends the file
                // on disk, so pending edits would be missing from it; the export reads the
                // document as it stands in memory, edits included, so there is nothing to ask.
                onExportMarkdown = {
                    if (viewModel.redactionMarkCount > 0) {
                        redactConfirm = RedactConfirm.EXPORT
                    } else {
                        exportMarkdown.launch(markdownExportName(state.displayName))
                    }
                },
                capabilities = viewModel.capabilities,
                isDynamicXfa = state.isDynamicXfa,
                isSignedOverwritePending = viewModel.isSignedOverwritePending,
                onConfirmSignedOverwrite = viewModel::confirmSignedOverwrite,
                onCancelSignedOverwrite = viewModel::cancelSignedOverwrite,
                hasDocumentFile = viewModel.hasDocumentFile,
                unlockPrompt = viewModel.unlockPrompt,
                passwordPrompt = viewModel.passwordPrompt,
                onStartUnlock = viewModel::startUnlock,
                onUnlock = viewModel::unlock,
                onCancelUnlock = viewModel::cancelUnlock,
                onStartPasswordCommand = viewModel::startPasswordCommand,
                onSetPassword = viewModel::setPassword,
                onRemovePassword = viewModel::removePassword,
                onCancelPasswordCommand = viewModel::cancelPasswordCommand,
                pageRewriteWarningShown = viewModel.pageRewriteWarningShown,
                onAnswerPageRewrite = viewModel::answerPageRewrite,
                onClose = viewModel::closeDocument,
                // Busy feedback (#145).
                busy = viewModel.busy,
                editingBlocked = viewModel.editingBlocked,
                toolsDisabled = viewModel.toolsDisabled,
                onCurrentPageChange = viewModel::onCurrentPageChanged,
                onSaveAndClose = viewModel::saveAndClose,
                // Share (#378): no unsaved changes shares immediately (same export as
                // Discard below — the on-disk file already is the current document).
                onShare = viewModel::shareLastSaved,
                onSaveAndShare = viewModel::saveAndShare,
                onShareLastSaved = viewModel::shareLastSaved,
                // Redaction (#173). Marks are drawn by the screen because the core never
                // writes them into the document.
                redactMode = viewModel.redactMode,
                redactionMarks = viewModel.redactionMarks,
                onToggleRedact = viewModel::toggleRedactMode,
                onMarkForRedaction = viewModel::markForRedaction,
                selectedRedactionMark = viewModel.selectedRedactionMark,
                onSelectRedactionMark = viewModel::selectRedactionMark,
                onRemoveRedactionMark = viewModel::removeRedactionMark,
                onClearRedactionMarks = viewModel::clearRedactionMarks,
                onCommitRedactionMarkRect = viewModel::commitRedactionMarkRect,
                // Whiteout (#3): real page content the moment it lands, so unlike a mark the
                // screen does not draw it — only the chrome once it is selected.
                whiteoutMode = viewModel.whiteoutMode,
                onToggleWhiteout = viewModel::toggleWhiteoutMode,
                onPlaceWhiteout = viewModel::placeWhiteout,
                selectedWhiteout = viewModel.selectedWhiteout,
                onCommitWhiteoutRect = viewModel::commitWhiteoutRect,
                onRemoveWhiteout = viewModel::removeSelectedWhiteout,
                // Reading mode (#507, #513).
                readingMode = viewModel.readingMode,
                onEnterReadingMode = viewModel::enterReadingMode,
                onExitReadingMode = viewModel::exitReadingMode,
                pinReadingBar = viewModel.screenshotPinsReadingBar,
                pageTint = viewModel.pageTint,
                onOpenSettings = { settingsOpen = true },
                // Page tools (#174).
                onOpenPages = viewModel::openPages,
            )

            // What the document's author asked, and the person's call (#558, ADR-004 decision 11).
            // Hosted here, beside the page-tool refusal below and for the same reason: it is asked
            // from the Pages grid as often as from the viewer, and it has to be seen over either.
            //
            // The wording reports a *request*, never a lock: these bits are not enforceable — any
            // tool with the owner password clears them, plenty of tools ignore them, and the person
            // holding the phone may be the author. So it says what was asked and then gets out of
            // the way. Continue is the confirm button because the question is "may I?", and it is
            // plain rather than emphasised: the recommended thing is to respect the request, and
            // going on is the deliberate second tap.
            viewModel.permissionQuestion?.let { klass ->
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { viewModel.answerPermission(false) },
                    title = {
                        androidx.compose.material3.Text(
                            stringResource(
                                when (klass) {
                                    PermissionClass.EDITING -> R.string.permission_ask_title_editing
                                    PermissionClass.ASSEMBLY -> R.string.permission_ask_title_assembly
                                    PermissionClass.EXTRACTION -> R.string.permission_ask_title_extraction
                                }
                            )
                        )
                    },
                    text = {
                        DialogBody {
                            androidx.compose.material3.Text(
                                stringResource(
                                    when (klass) {
                                        PermissionClass.EDITING -> R.string.permission_ask_editing
                                        PermissionClass.ASSEMBLY -> R.string.permission_ask_assembly
                                        PermissionClass.EXTRACTION -> R.string.permission_ask_extraction
                                    }
                                )
                            )
                            // One shared sentence rather than three: it says the same thing in
                            // every case — that this is a request and that the answer is
                            // remembered — and three copies of it in three languages would drift.
                            androidx.compose.material3.Text(stringResource(R.string.permission_ask_note))
                        }
                    },
                    confirmButton = {
                        androidx.compose.material3.TextButton(
                            onClick = { viewModel.answerPermission(true) },
                        ) { androidx.compose.material3.Text(stringResource(R.string.action_continue)) }
                    },
                    dismissButton = {
                        androidx.compose.material3.TextButton(
                            onClick = { viewModel.answerPermission(false) },
                        ) { androidx.compose.material3.Text(stringResource(R.string.cancel)) }
                    },
                )
            }

            // A page tool refused (#174): what happened, why, and that nothing was changed — the
            // shape the permission question above and the redaction refusal below both follow,
            // because a refusal is information.
            // Hosted here rather than in either screen so it is shown over whichever is up.
            viewModel.pageToolRefusal?.let { refusal ->
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { viewModel.pageToolRefusal = null },
                    title = { androidx.compose.material3.Text(stringResource(R.string.pages_refused_title)) },
                    text = {
                        androidx.compose.material3.Text(viewModel.describePageToolRefusal(refusal))
                    },
                    confirmButton = {
                        androidx.compose.material3.TextButton(
                            onClick = { viewModel.pageToolRefusal = null },
                        ) { androidx.compose.material3.Text(stringResource(R.string.redact_ok)) }
                    },
                )
            }

            // The confirmation #173 asks for, before either save path writes anything:
            // what redaction does, that it cannot be undone once saved, and Save a copy as
            // the default action — the reversible choice, because the other one cannot be
            // taken back.
            //
            // Opened for an export (#409), the same question ends in Export as Markdown or
            // Cancel: the marked content is removed from the document in memory and the
            // export then reads it — and the document is left with unsaved changes, because
            // no PDF was written (see [ViewerViewModel.applyRedactionsForExport]).
            redactConfirm?.let { ask ->
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { redactConfirm = null },
                    title = { androidx.compose.material3.Text(stringResource(R.string.redact_confirm_title)) },
                    text = {
                        androidx.compose.material3.Text(
                            stringResource(R.string.redact_confirm_body) + "\n\n" +
                                if (viewModel.redactionMarkCount == 1) {
                                    stringResource(R.string.redact_mark_count_one)
                                } else {
                                    stringResource(
                                        R.string.redact_mark_count,
                                        viewModel.redactionMarkCount.toString(),
                                    )
                                },
                        )
                    },
                    confirmButton = {
                        androidx.compose.material3.TextButton(onClick = {
                            redactConfirm = null
                            scope.launch {
                                when (ask) {
                                    RedactConfirm.SAVE ->
                                        if (viewModel.applyRedactions()) createDocument.launch(redactedName(state.displayName))
                                    RedactConfirm.EXPORT ->
                                        if (viewModel.applyRedactionsForExport()) exportMarkdown.launch(markdownExportName(state.displayName))
                                }
                            }
                        }) {
                            androidx.compose.material3.Text(
                                stringResource(
                                    when (ask) {
                                        RedactConfirm.SAVE -> R.string.redact_save_copy
                                        RedactConfirm.EXPORT -> R.string.export_markdown
                                    }
                                )
                            )
                        }
                    },
                    dismissButton = {
                        androidx.compose.material3.TextButton(onClick = {
                            redactConfirm = null
                            if (ask == RedactConfirm.SAVE) {
                                scope.launch { if (viewModel.applyRedactions()) viewModel.save() }
                            }
                        }) {
                            androidx.compose.material3.Text(
                                stringResource(
                                    when (ask) {
                                        RedactConfirm.SAVE -> R.string.redact_overwrite
                                        RedactConfirm.EXPORT -> R.string.cancel
                                    }
                                )
                            )
                        }
                    },
                )
            }

            // The summary after saving, and the refusal when a redaction removed nothing.
            viewModel.redactionSummary?.let { summary ->
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { viewModel.redactionSummary = null },
                    text = { androidx.compose.material3.Text(summary) },
                    confirmButton = {
                        androidx.compose.material3.TextButton(
                            onClick = { viewModel.redactionSummary = null },
                        ) { androidx.compose.material3.Text(stringResource(R.string.redact_ok)) }
                    },
                )
            }
            // A document handed in from another app (#376) while this one has unsaved
            // changes: same Save/Discard/Cancel shape as closing the viewer by hand, since
            // silently dropping an edit for a PDF that just arrived by mail would surprise
            // someone worse than asking.
            if (viewModel.pendingExternalOpen != null) {
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = viewModel::cancelExternalOpen,
                    title = { androidx.compose.material3.Text(stringResource(R.string.unsaved_changes)) },
                    text = { androidx.compose.material3.Text(stringResource(R.string.unsaved_changes_body)) },
                    confirmButton = {
                        androidx.compose.material3.TextButton(onClick = viewModel::saveAndOpenExternal) {
                            androidx.compose.material3.Text(stringResource(R.string.save))
                        }
                    },
                    dismissButton = {
                        androidx.compose.foundation.layout.Row {
                            androidx.compose.material3.TextButton(onClick = viewModel::cancelExternalOpen) {
                                androidx.compose.material3.Text(stringResource(R.string.cancel))
                            }
                            androidx.compose.material3.TextButton(onClick = viewModel::discardAndOpenExternal) {
                                androidx.compose.material3.Text(stringResource(R.string.discard))
                            }
                        }
                    },
                )
            }

            viewModel.redactionRefusal?.let { refusal ->
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { viewModel.redactionRefusal = null },
                    title = { androidx.compose.material3.Text(stringResource(R.string.redact_refused_title)) },
                    text = { androidx.compose.material3.Text(viewModel.describeRefusal(refusal)) },
                    confirmButton = {
                        androidx.compose.material3.TextButton(
                            onClick = { viewModel.redactionRefusal = null },
                        ) { androidx.compose.material3.Text(stringResource(R.string.redact_ok)) }
                    },
                )
            }
        }
    }
}
