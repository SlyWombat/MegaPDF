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
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
        // (`MaterialTheme {}` takes the default light scheme regardless of the
        // system setting), so the automatic style would paint white icons onto a
        // white app whenever the device is in dark mode.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        val screenshotState = intent.getStringExtra("screenshot")
        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    MegaPdfApp(screenshotState = screenshotState)
                }
            }
        }
    }
}

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

    // One-shot status toasts ("Saved", save errors).
    val context = LocalContext.current
    val status = viewModel.statusMessage
    LaunchedEffect(status) {
        if (status != null) {
            Toast.makeText(context, status, Toast.LENGTH_SHORT).show()
            viewModel.consumeStatus()
        }
    }

    when (val state = viewModel.uiState) {
        is ViewerUiState.Home -> HomeScreen(
            recents = state.recents,
            error = state.error,
            onOpenClick = { openDocument.launch(arrayOf("application/pdf")) },
            onRecentClick = viewModel::openRecent,
        )

        is ViewerUiState.Loading -> LoadingScreen()

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
                canUndo = viewModel.canUndo,
                canRedo = viewModel.canRedo,
                pendingTextTap = viewModel.pendingTextTap,
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
                screenshotSheet = viewModel.screenshotSheet,
                onUndo = viewModel::undo,
                onRedo = viewModel::redo,
                onStartTextPlacement = viewModel::startTextPlacement,
                onCommitText = viewModel::commitText,
                onCancelTextPlacement = viewModel::cancelTextPlacement,
                onCommitStampRect = viewModel::commitStampRect,
                onRemoveStamp = viewModel::removeSelectedStamp,
                onSave = viewModel::save,
                onSaveAs = { createDocument.launch(state.displayName) },
                onClose = viewModel::closeDocument,
            )
        }
    }
}
