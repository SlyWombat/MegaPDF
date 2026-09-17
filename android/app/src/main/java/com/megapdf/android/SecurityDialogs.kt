package com.megapdf.android

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp

// Document security dialogs (#131). What is typed lives only in these composables'
// remembered state, so it goes when the dialog does; the view model hands it straight to
// the engine and keeps no copy (ADR-004 decision 1).

/**
 * Asks for a restricted document's owner password (ADR-004 decision 3), modelled on
 * [PasswordDialog]. A wrong password keeps the dialog and clears the field; Cancel keeps
 * the restricted document open.
 */
@Composable
fun UnlockDialog(
    prompt: UnlockPrompt,
    discardsChanges: Boolean,
    onSubmit: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    // Keyed on the attempt, so a wrong password empties the field.
    var typed by remember(prompt.attempt) { mutableStateOf("") }
    AlertDialog(
        modifier = Modifier.imePadding(),
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.security_unlock_title)) },
        text = {
            DialogBody {
                Text(stringResource(R.string.security_unlock_body))
                if (discardsChanges) Text(stringResource(R.string.security_unlock_discards))
                if (prompt.wrongPassword) {
                    Text(
                        stringResource(R.string.security_unlock_wrong),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
                SecretField(
                    value = typed,
                    onValueChange = { typed = it },
                    label = stringResource(R.string.security_owner_password),
                    enabled = !prompt.isChecking,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onSubmit(typed) }, enabled = typed.isNotEmpty() && !prompt.isChecking) {
                Text(stringResource(R.string.security_unlock))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

/**
 * The Password command (ADR-004 decision 5): set one password on an unprotected document,
 * change or remove it with full access, or — without full access — explain and offer the
 * owner password.
 */
@Composable
fun DocumentPasswordDialog(
    mode: PasswordCommandMode,
    onSet: (String) -> Unit,
    onRemove: () -> Unit,
    onUnlock: () -> Unit,
    onDismiss: () -> Unit,
) {
    when (mode) {
        PasswordCommandMode.RESTRICTED -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(stringResource(R.string.security_password_title)) },
            text = { DialogBody { Text(stringResource(R.string.security_password_restricted_body)) } },
            confirmButton = {
                TextButton(onClick = onUnlock) { Text(stringResource(R.string.security_unlock_ellipsis)) }
            },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
        )
        PasswordCommandMode.SET, PasswordCommandMode.CHANGE ->
            NewPasswordDialog(changing = mode == PasswordCommandMode.CHANGE, onSet, onRemove, onDismiss)
    }
}

@Composable
private fun NewPasswordDialog(
    changing: Boolean,
    onSet: (String) -> Unit,
    onRemove: () -> Unit,
    onDismiss: () -> Unit,
) {
    var typed by remember { mutableStateOf("") }
    var confirmation by remember { mutableStateOf("") }
    // Shown once the user tries to submit, and cleared as soon as they type again.
    var problem by remember { mutableStateOf<NewPasswordProblem?>(null) }
    AlertDialog(
        modifier = Modifier.imePadding(),
        onDismissRequest = onDismiss,
        title = {
            Text(stringResource(if (changing) R.string.security_password_title else R.string.security_set_title))
        },
        text = {
            DialogBody {
                Text(stringResource(if (changing) R.string.security_change_body else R.string.security_set_body))
                SecretField(
                    value = typed,
                    onValueChange = { typed = it; problem = null },
                    label = stringResource(if (changing) R.string.security_new_password else R.string.password),
                    isError = problem == NewPasswordProblem.EMPTY,
                )
                SecretField(
                    value = confirmation,
                    onValueChange = { confirmation = it; problem = null },
                    label = stringResource(R.string.security_confirm_password),
                    isError = problem == NewPasswordProblem.MISMATCH,
                )
                problem?.let {
                    Text(
                        stringResource(
                            when (it) {
                                NewPasswordProblem.EMPTY -> R.string.security_password_empty
                                NewPasswordProblem.MISMATCH -> R.string.security_password_mismatch
                            }
                        ),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                if (changing) {
                    // In the body rather than a third dialog button: three long labels do
                    // not fit a phone's button row, least of all in French.
                    TextButton(onClick = onRemove) { Text(stringResource(R.string.security_remove)) }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = {
                val found = newPasswordProblem(typed, confirmation)
                if (found != null) problem = found else onSet(typed)
            }) {
                Text(stringResource(if (changing) R.string.security_change else R.string.security_set))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

/** A masked, single-line field whose keyboard neither suggests nor learns what is typed. */
@Composable
private fun SecretField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    enabled: Boolean = true,
    isError: Boolean = false,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        visualTransformation = PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        singleLine = true,
        enabled = enabled,
        isError = isError,
        modifier = Modifier.fillMaxWidth(),
    )
}
