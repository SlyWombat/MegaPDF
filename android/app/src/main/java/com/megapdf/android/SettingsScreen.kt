package com.megapdf.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.megapdf.engine.PageTint

/**
 * Settings (#513). MegaPDF for Android had no settings screen at all before reading mode —
 * everything it knew was either a document's own business or a store of records — so this is
 * the first, and it opens with the one group reading mode asks for.
 *
 * Both rows are app-level, not per document (#168 decision 2), and both are backed by
 * [ReadingPreferences]'s `DataStore<Preferences>`: three scalars a screen binds to, which is
 * what `DataStore<Preferences>` is for and what the plan's "Storage" section calls for.
 *
 * A full-screen overlay with its own `Scaffold`, drawn over whatever was underneath, the way
 * [ThirdPartyNoticesScreen] is — the app is one activity and one back stack.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    pageTint: PageTint,
    openInReadingMode: Boolean,
    onPageTintChange: (PageTint) -> Unit,
    onOpenInReadingModeChange: (Boolean) -> Unit,
    onClose: () -> Unit,
) {
    androidx.activity.compose.BackHandler(onBack = onClose)
    Surface(Modifier.fillMaxSize()) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text(stringResource(R.string.settings)) },
                    navigationIcon = {
                        IconButton(onClick = onClose) {
                            Icon(
                                Icons.AutoMirrored.Filled.ArrowBack,
                                contentDescription = stringResource(R.string.back),
                            )
                        }
                    },
                )
            },
        ) { padding ->
            Column(
                Modifier
                    .fillMaxSize()
                    .padding(padding)
                    .verticalScroll(rememberScrollState())
                    .padding(PaddingValues(vertical = 8.dp)),
            ) {
                GroupHeading(stringResource(R.string.settings_reading))

                SettingLabel(stringResource(R.string.page_colours))
                // A radio group, not a dropdown: three mutually exclusive choices, all of
                // them worth seeing at once, and a screen reader reads the group and the
                // chosen one rather than "button, Normal".
                Column(Modifier.selectableGroup()) {
                    PageTintRow(PageTint.NORMAL, R.string.page_colours_normal, pageTint, onPageTintChange)
                    PageTintRow(PageTint.SEPIA, R.string.page_colours_sepia, pageTint, onPageTintChange)
                    PageTintRow(PageTint.NIGHT, R.string.page_colours_night, pageTint, onPageTintChange)
                }
                // The trade-off is a decision, not a defect (#168 decision 3): the post-pass
                // inverts everything the page drew, so a photograph reads as a negative.
                // Said here because someone choosing Night should not have to discover it.
                Text(
                    stringResource(R.string.night_inverts_pictures),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 4.dp, bottom = 8.dp),
                )

                SwitchRow(
                    label = stringResource(R.string.open_in_reading_mode),
                    checked = openInReadingMode,
                    onCheckedChange = onOpenInReadingModeChange,
                )
            }
        }
    }
}

@Composable
private fun GroupHeading(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 4.dp),
    )
}

@Composable
private fun SettingLabel(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodyLarge,
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp),
    )
}

@Composable
private fun PageTintRow(
    tint: PageTint,
    labelId: Int,
    selected: PageTint,
    onSelect: (PageTint) -> Unit,
) {
    val on = tint == selected
    androidx.compose.foundation.layout.Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            // The whole row is the target, and `selectable` with Role.RadioButton is what
            // puts "selected" in the accessibility tree rather than leaving it in the ink.
            .selectable(selected = on, role = Role.RadioButton, onClick = { onSelect(tint) })
            .padding(horizontal = 16.dp, vertical = 4.dp),
    ) {
        RadioButton(selected = on, onClick = null)
        Text(
            stringResource(labelId),
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(start = 12.dp),
        )
    }
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    androidx.compose.foundation.layout.Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
        modifier = Modifier
            .fillMaxWidth()
            .toggleableRow(checked, onCheckedChange)
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        Text(label, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
        // onCheckedChange = null: the row above owns the toggle, so the switch is ink, not a
        // second node a screen reader would land on and read as an unlabelled control.
        Switch(checked = checked, onCheckedChange = null)
    }
}

/** The row, not the switch, is the target — 48 dp of it, and it carries the label. */
private fun Modifier.toggleableRow(checked: Boolean, onCheckedChange: (Boolean) -> Unit) =
    toggleable(
        value = checked,
        role = Role.Switch,
        onValueChange = onCheckedChange,
    )
