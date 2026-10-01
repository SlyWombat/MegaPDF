namespace MegaPDF.Core.Engine;

/// <summary>
/// What can honestly be said about a digital signature the document already carries
/// (#576), so a question about removing it can name which signature is meant.
///
/// <para>Not a verdict on validity. The core verifies nothing, and #476 settled that a
/// signature cannot survive a MegaPDF save at all — measured 33 of 33 real signed
/// documents going from valid to digest mismatch with no edit — and closed preserving one
/// as an accepted, permanent limitation. So a signature a document carries is either about
/// to be invalidated by the save, or was invalidated by an earlier one.</para>
///
/// <para>What is here is what PDFium hands over for the cost of a dictionary read: the
/// signing time and the reason. What is deliberately absent is the signer's <i>name</i>,
/// which lives in the certificate's subject inside the PKCS#7 blob and would mean ASN.1
/// and X.509 parsing in shared code that five platforms link. Both of these are present on
/// 33 of 33 of #476's genuinely signed documents (measured for #576), so naming a
/// signature by its date and the signer's own stated reason is not a fallback — it is what
/// real documents actually say.</para>
/// </summary>
/// <param name="SignedOn">
/// The signer's own stated signing time, from the signature's <c>/M</c> entry, or null when
/// it records none. Kept as a <see cref="DateTimeOffset"/> with the signer's stated offset
/// rather than converted: a signature says when its signer thought they signed, and
/// showing that moment in the reader's timezone would be showing a different claim.
/// </param>
/// <param name="Reason">
/// The signer's own words about why they signed (<c>/Reason</c>), or null when there are
/// none. Real ones run long — the two that #476's corpus carries are 86 and 118 characters
/// — so anything showing this must expect a sentence, not a label.
/// </param>
/// <param name="IsCertification">
/// True when this is a certification signature carrying a <c>/DocMDP</c> transform rather
/// than an ordinary approval signature: its author declared the document closed to changes
/// of some kind. Measured 33 of 33 across #476's real corpus, every one at permission 1
/// ("no changes allowed").
/// </param>
public sealed record PdfDigitalSignature(DateTimeOffset? SignedOn, string? Reason, bool IsCertification)
{
    /// <summary>
    /// True when there is something to name the signature by. Nothing in the app depends on
    /// it being true — the choice being offered, remove it or keep it, does not need the
    /// signature identified — but the wording says less when it is false.
    /// </summary>
    public bool HasDetail => SignedOn is not null || !string.IsNullOrWhiteSpace(Reason);
}
