package com.megapdf.android

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.text.DateFormat
import java.util.Date
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics

@Composable
fun HomeScreen(
    recents: List<RecentRow>,
    error: String?,
    onOpenClick: () -> Unit,
    onRecentClick: (RecentEntry) -> Unit,
    onRemoveRecent: (RecentEntry) -> Unit = {},
) {
    var aboutOpen by remember { mutableStateOf(false) }
    var noticesOpen by remember { mutableStateOf(false) }
    // #165: there is no hover on a phone, so the location lives in the row and the
    // rest behind a long press.
    var sheetFor by remember { mutableStateOf<RecentRow?>(null) }

    // The only screen without a Scaffold, so nothing else applies window insets
    // to it (#40). Without this the About button sits under the status bar once
    // edge-to-edge is on.
    Box(Modifier.fillMaxSize().safeDrawingPadding()) {
        HomeContent(
            recents = recents,
            error = error,
            onOpenClick = onOpenClick,
            onRecentClick = onRecentClick,
            onRecentLongPress = { sheetFor = it },
        )
        IconButton(
            onClick = { aboutOpen = true },
            modifier = Modifier.align(Alignment.TopEnd).padding(8.dp),
        ) {
            Icon(
                Icons.Outlined.Info,
                contentDescription = stringResource(R.string.about_megapdf),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    // Outside the inset Box on purpose: this is a full-screen overlay with its own
    // Scaffold, so leaving it inside would apply the window insets twice (#40).
    if (noticesOpen) {
        ThirdPartyNoticesScreen(onClose = { noticesOpen = false })
    }

    sheetFor?.let { row ->
        RecentDetailsSheet(
            row = row,
            onOpen = { sheetFor = null; onRecentClick(row.entry) },
            onRemove = { sheetFor = null; onRemoveRecent(row.entry) },
            onDismiss = { sheetFor = null },
        )
    }

    if (aboutOpen) {
        AboutDialog(
            onDismiss = { aboutOpen = false },
            onShowNotices = {
                aboutOpen = false
                noticesOpen = true
            },
        )
    }
}

@Composable
private fun HomeContent(
    recents: List<RecentRow>,
    error: String?,
    onOpenClick: () -> Unit,
    onRecentClick: (RecentEntry) -> Unit,
    onRecentLongPress: (RecentRow) -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(48.dp))
        Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineLarge)
        Text(
            stringResource(R.string.tagline),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(Modifier.height(24.dp))
        Button(onClick = onOpenClick) { Text(stringResource(R.string.open_pdf)) }

        if (error != null) {
            Spacer(Modifier.height(16.dp))
            Text(
                error,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        if (recents.isNotEmpty()) {
            Spacer(Modifier.height(32.dp))
            Text(
                stringResource(R.string.recent),
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(recents, key = { it.entry.uri }) { row ->
                    RecentCard(
                        row = row,
                        onClick = { onRecentClick(row.entry) },
                        onLongClick = { onRecentLongPress(row) },
                    )
                }
            }
        }
    }
}

/**
 * One recent document: its name, and underneath it where the file lives (#165).
 *
 * Two lines, not one, and on every row rather than only the ambiguous ones — the
 * list stays the same shape whatever is in it, which is what makes it scannable.
 * A file whose grant has gone says so in place of its location and is dimmed; it
 * stays on the list, because a row that vanishes is a row nobody can remove on
 * purpose.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun RecentCard(row: RecentRow, onClick: () -> Unit, onLongClick: () -> Unit) {
    val entry = row.entry
    val location = RecentLocation.format(
        RecentLocation.localised(entry.location, entry.authority,
            stringResource(R.string.location_downloads)),
        maxSegments = 3)
    val supporting = when {
        !row.available -> stringResource(R.string.recent_not_found)
        location.isNotEmpty() -> location
        else -> DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT)
            .format(Date(entry.lastOpenedEpochMs))
    }
    val describe = when {
        !row.available -> stringResource(R.string.recent_row_a11y_not_found, entry.displayName)
        location.isNotEmpty() -> stringResource(R.string.recent_row_a11y, entry.displayName, location)
        // No location known: the name is the whole accessible name.
        else -> entry.displayName
    }
    val dim = if (row.available) 1f else 0.55f
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = onClick, onLongClick = onLongClick)
            .semantics { contentDescription = describe },
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(
                entry.displayName,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = dim),
                maxLines = 1,
                softWrap = false,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                supporting,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = dim),
                // The location loses its middle rather than its end: the innermost
                // folder is usually what tells two same-named files apart.
                maxLines = 1,
                softWrap = false,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * What a long press on a recent row offers (#165): where the file is, in full,
 * and the way to take it off the list. There is no hover on a phone, so this is
 * where the detail lives.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RecentDetailsSheet(
    row: RecentRow,
    onOpen: () -> Unit,
    onRemove: () -> Unit,
    onDismiss: () -> Unit,
) {
    val location = RecentLocation.format(
        RecentLocation.localised(row.entry.location, row.entry.authority,
            stringResource(R.string.location_downloads)),
        maxSegments = Int.MAX_VALUE)
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp)
                .navigationBarsPadding(),
        ) {
            Text(row.entry.displayName, style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(8.dp))
            Text(
                when {
                    !row.available -> stringResource(R.string.recent_not_found)
                    location.isNotEmpty() -> location
                    else -> stringResource(R.string.recent_location_unknown)
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(16.dp))
            if (row.available) {
                TextButton(onClick = onOpen) { Text(stringResource(R.string.recent_open)) }
            }
            TextButton(onClick = onRemove) {
                Text(stringResource(R.string.recent_remove), color = MaterialTheme.colorScheme.error)
            }
            Spacer(Modifier.height(16.dp))
        }
    }
}

@Composable
fun LoadingScreen(indicator: BusyIndicator? = null) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        if (indicator == null) {
            CircularProgressIndicator()
        } else if (indicator.isVisible) {
            // #145: nothing for the first half second, then the spinner with what it is doing,
            // read out by TalkBack without taking focus.
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                CircularProgressIndicator()
                indicator.label?.let {
                    Text(
                        stringResource(it.stringId),
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                    )
                }
            }
        }
    }
}

@Composable
fun PasswordDialog(
    wrongPassword: Boolean,
    onSubmit: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var password by remember { mutableStateOf("") }
    AlertDialog(
        modifier = Modifier.imePadding(),
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.password_required)) },
        text = {
            DialogBody {
                if (wrongPassword) {
                    Text(
                        stringResource(R.string.wrong_password),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text(stringResource(R.string.password)) },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                )
            }
        },
        confirmButton = { TextButton(onClick = { onSubmit(password) }) { Text(stringResource(R.string.open)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
