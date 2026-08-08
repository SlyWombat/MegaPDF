package com.megapdf.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.lifecycle.viewmodel.compose.viewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    MegaPdfApp()
                }
            }
        }
    }
}

@Composable
fun MegaPdfApp(viewModel: ViewerViewModel = viewModel()) {
    val openDocument = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri -> uri?.let { viewModel.openUri(it) } }

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
            BackHandler { viewModel.closeDocument() }
            ViewerScreen(
                displayName = state.displayName,
                pageSizes = state.pageSizes,
                pageBitmaps = viewModel.pageBitmaps,
                onRenderWindowChange = viewModel::updateRenderWindow,
                onPageTap = viewModel::onPageTapped,
                onClose = viewModel::closeDocument,
            )
        }
    }
}
