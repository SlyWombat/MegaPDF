using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The warning before Save overwrites a signed original (#476, #481, SDD-adjacent to
/// #173's ConfirmRedactionWindow), and since #576 the place the person is offered the
/// removal of a signature the save is about to leave invalid. Save a copy is the default
/// action and the others are deliberate — Dave's framing is that the common path, saving a
/// copy under your own name, is already safe, so the destructive path is the one that needs
/// the extra click. Cancel, Escape and closing the window change nothing: nothing is saved.
/// </summary>
public partial class ConfirmSignedSaveWindow : Window
{
    public enum Decision { Cancel, SaveAsCopy, Overwrite }

    internal Decision Choice { get; private set; } = Decision.Cancel;

    /// <summary>
    /// Whether the save should leave the signature out — the tick #576 added to this
    /// conversation, read alongside <see cref="Choice"/> rather than being a choice of its
    /// own. Meaningless after Cancel, because nothing is saved.
    /// </summary>
    internal bool RemoveSignature => RemoveSignatureCheck.IsChecked == true;

    public ConfirmSignedSaveWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        SaveCopyButton.Click += (_, _) => Answer(Decision.SaveAsCopy);
        OverwriteButton.Click += (_, _) => Answer(Decision.Overwrite);
    }

    private void Answer(Decision choice)
    {
        Choice = choice;
        Close();
    }

    /// <summary>
    /// Sets the title/body for whichever case applies. A certification signature
    /// (`/DocMDP`) gets different wording — measured (#476/#481) as the common case on
    /// real signed documents, not the rare one: its own author declared the document
    /// closed to modification outright, a stronger statement than "this will invalidate
    /// the signature".
    /// </summary>
    internal void SetCertification(bool isCertified)
    {
        Title = isCertified ? Strings.CertifiedSaveWarningTitle : Strings.SignedSaveWarningTitle;
        PromptText.Text = Title;
        BodyText.Text = isCertified ? Strings.CertifiedSaveWarningBody : Strings.SignedSaveWarningBody;
    }

    /// <summary>
    /// Names the signature the question is about (#576), from what the document itself
    /// records: the signing date and the signer's own reason. Nothing else is available —
    /// the signer's name lives in the certificate inside the signature blob, which would
    /// mean ASN.1 and X.509 parsing in shared engine code (see
    /// <see cref="PdfDigitalSignature"/>), and the choice being offered does not depend on
    /// it.
    ///
    /// <para>With more than one signature, the count is given instead of naming one:
    /// "signed on …" beside four signatures would be naming the first and implying it is
    /// the only one.</para>
    /// </summary>
    internal void SetSignatures(IReadOnlyList<PdfDigitalSignature> signatures)
    {
        SignatureText.Text = Describe(signatures);
        SignatureText.IsVisible = SignatureText.Text.Length > 0;
    }

    internal static string Describe(IReadOnlyList<PdfDigitalSignature> signatures)
    {
        if (signatures.Count > 1)
            return Strings.SignedSaveSeveralSignatures(signatures.Count);
        if (signatures.Count == 0)
            return "";
        var signature = signatures[0];
        var parts = new List<string>(2);
        if (signature.SignedOn is { } on)
        {
            // The signer's own stated date, in the reader's language: the moment the
            // signature records, shown as a date rather than converted to the reader's
            // timezone, which would be showing a different claim.
            parts.Add(Strings.SignedSaveSignedOn(on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)));
        }
        if (!string.IsNullOrWhiteSpace(signature.Reason))
            parts.Add(Strings.SignedSaveSignerReason(signature.Reason));
        return string.Join(" ", parts);
    }
}
