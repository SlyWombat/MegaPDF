package com.megapdf.android

import android.graphics.Color
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.enableEdgeToEdge
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
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
        setContent {
            MegaPdfTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    MegaPdfApp(screenshotState = screenshotState)
                }
            }
        }
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

@Composable
fun MegaPdfApp(viewModel: ViewerViewModel = viewModel(), screenshotState: String? = null) {
    LaunchedEffect(screenshotState) { viewModel.applyScreenshotMode(screenshotState) }
    val openDocument = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri -> uri?.let { viewModel.openUri(it) } }
    val createDocument = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/pdf")
    ) { uri -> uri?.let { viewModel.saveAs(it) } }
    val pickSignatureImage = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia()
    ) { uri -> uri?.let { viewModel.importSignature(it) } }

    // The Redact confirmation is open (#173): marks are on the document and a save has
    // been asked for, so the question comes before anything is written.
    var redactConfirmOpen by androidx.compose.runtime.remember {
        androidx.compose.runtime.mutableStateOf(false)
    }
    val scope = androidx.compose.runtime.rememberCoroutineScope()

    // One-shot status toasts ("Saved", save errors).
    val context = LocalContext.current
    val status = viewModel.statusMessage
    LaunchedEffect(status) {
        if (status != null) {
            Toast.makeText(context, status, Toast.LENGTH_SHORT).show()
            viewModel.consumeStatus()
        }
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

    when (val state = viewModel.uiState) {
        is ViewerUiState.Home -> HomeScreen(
            recents = state.recents,
            error = state.error,
            onOpenClick = { openDocument.launch(arrayOf("application/pdf")) },
            onRecentClick = viewModel::openRecent,
            onRemoveRecent = viewModel::removeRecent,
        )

        is ViewerUiState.Loading -> LoadingScreen(viewModel.busy.document)

        is ViewerUiState.PasswordNeeded -> PasswordDialog(
            wrongPassword = state.wrongPassword,
            onSubmit = { password -> viewModel.openUri(state.uri, password) },
            onDismiss = viewModel::closeDocument,
        )

        is ViewerUiState.Viewing -> {
            ViewerScreen(
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
                    if (viewModel.redactionMarkCount > 0) redactConfirmOpen = true else viewModel.save()
                },
                onSaveAs = {
                    if (viewModel.redactionMarkCount > 0) {
                        redactConfirmOpen = true
                    } else {
                        createDocument.launch(state.displayName)
                    }
                },
                capabilities = viewModel.capabilities,
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
            )

            // The confirmation #173 asks for, before either save path writes anything:
            // what redaction does, that it cannot be undone once saved, and Save a copy as
            // the default action — the reversible choice, because the other one cannot be
            // taken back.
            if (redactConfirmOpen) {
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { redactConfirmOpen = false },
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
                            redactConfirmOpen = false
                            scope.launch {
                                if (viewModel.applyRedactions()) createDocument.launch(redactedName(state.displayName))
                            }
                        }) { androidx.compose.material3.Text(stringResource(R.string.redact_save_copy)) }
                    },
                    dismissButton = {
                        androidx.compose.material3.TextButton(onClick = {
                            redactConfirmOpen = false
                            scope.launch { if (viewModel.applyRedactions()) viewModel.save() }
                        }) { androidx.compose.material3.Text(stringResource(R.string.redact_overwrite)) }
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
