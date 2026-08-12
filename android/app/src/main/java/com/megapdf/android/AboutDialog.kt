package com.megapdf.android

import android.content.pm.PackageManager
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private const val PROJECT_URL = "https://github.com/SlyWombat/MegaPDF"
private const val NOTICES_ASSET = "THIRD-PARTY-NOTICES.txt"

@Composable
fun AboutDialog(
    onDismiss: () -> Unit,
    onShowNotices: () -> Unit,
) {
    val context = LocalContext.current
    val uriHandler = LocalUriHandler.current
    val versionName = remember(context) {
        try {
            context.packageManager.getPackageInfo(context.packageName, 0).versionName
        } catch (_: PackageManager.NameNotFoundException) {
            null
        }
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("MegaPDF") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (versionName != null) {
                    Text(
                        "Version $versionName",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Text(
                    "Copyright © 2026 ElectricRV.ca Corporation. All rights reserved.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                Text(
                    "Special thanks to Mega Woman.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                TextButton(onClick = { uriHandler.openUri(PROJECT_URL) }) {
                    Text("github.com/SlyWombat/MegaPDF")
                }
                TextButton(onClick = onShowNotices) {
                    Text("Third-party notices")
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ThirdPartyNoticesScreen(onClose: () -> Unit) {
    val context = LocalContext.current
    var paragraphs by remember { mutableStateOf<List<String>?>(null) }
    // The notices file is ~150 KB; read and split it off the main thread.
    LaunchedEffect(Unit) {
        paragraphs = withContext(Dispatchers.IO) {
            val text = context.assets.open(NOTICES_ASSET)
                .bufferedReader()
                .use { it.readText() }
            splitNoticeParagraphs(text)
        }
    }
    androidx.activity.compose.BackHandler(onBack = onClose)
    Surface(Modifier.fillMaxSize()) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text("Third-party notices") },
                    navigationIcon = {
                        IconButton(onClick = onClose) {
                            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                        }
                    },
                )
            },
        ) { padding ->
            val current = paragraphs
            if (current == null) {
                Box(
                    Modifier.fillMaxSize().padding(padding),
                    contentAlignment = Alignment.Center,
                ) {
                    CircularProgressIndicator()
                }
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize().padding(padding),
                    contentPadding = PaddingValues(16.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    items(current.size) { index ->
                        Text(
                            current[index],
                            style = MaterialTheme.typography.bodySmall,
                            fontFamily = FontFamily.Monospace,
                        )
                    }
                }
            }
        }
    }
}

// Splits the notices text into blank-line-separated blocks so the ~150 KB file
// renders as many small LazyColumn items instead of one giant Text.
internal fun splitNoticeParagraphs(text: String): List<String> =
    text.split(Regex("\\r?\\n(?:[ \\t]*\\r?\\n)+"))
        .map { it.trimEnd() }
        .filter { it.isNotBlank() }
