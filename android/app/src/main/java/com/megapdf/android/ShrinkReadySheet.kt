package com.megapdf.android

import android.text.format.Formatter
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

/**
 * The result of Shrink for email (#593): "Was X, now Y", the reassurance that the open document
 * is unchanged, then Share (the emailing case, so the filled button) and Save a copy.
 *
 * Swiping it away or tapping the scrim is [onDismiss], which drops the copy. Share and Save a
 * copy hide the sheet with its own animation first and then hand over; Save a copy does not
 * drop the copy, because the picker it opens still needs it (its back-out drops it instead).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShrinkReadySheet(
    ready: ViewerViewModel.ShrinkReady,
    onShare: () -> Unit,
    onSave: () -> Unit,
    onDismiss: () -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val hideThen: (() -> Unit) -> Unit = { after ->
        scope.launch { sheetState.hide() }.invokeOnCompletion { after() }
    }

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
                stringResource(R.string.shrink_ready_title),
                style = MaterialTheme.typography.titleLarge,
            )
            Spacer(Modifier.height(12.dp))
            // The platform formatter localises the units ("12,4 Mo" in French).
            Text(
                stringResource(
                    R.string.shrink_ready_body,
                    Formatter.formatShortFileSize(context, ready.beforeBytes),
                    Formatter.formatShortFileSize(context, ready.afterBytes),
                ),
                style = MaterialTheme.typography.bodyLarge,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                stringResource(R.string.shrink_original_unchanged),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(24.dp))
            Button(
                onClick = { hideThen(onShare) },
                modifier = Modifier.fillMaxWidth(),
            ) { Text(stringResource(R.string.share)) }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(
                onClick = { hideThen(onSave) },
                modifier = Modifier.fillMaxWidth(),
            ) { Text(stringResource(R.string.save_a_copy)) }
            Spacer(Modifier.height(16.dp))
        }
    }
}
