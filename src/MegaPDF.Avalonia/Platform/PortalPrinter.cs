using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Printing from inside a Flatpak sandbox or the snap, through
/// <c>org.freedesktop.portal.Print</c> (#158).
///
/// **Why a portal at all.** The sandbox has no <c>lp</c> and no CUPS socket, and
/// giving it one would mean <c>--socket=cups</c> or <c>--filesystem=/run/cups</c> —
/// a hole in the thing that makes "no network access, no blanket file access" true
/// in the listing. The portal is the route the sandbox is designed around: the app
/// hands the desktop a file descriptor and the desktop prints it, with its own
/// dialog, its own printer list and its own permissions. Nothing is added to
/// <c>finish-args</c> for this: talking to <c>org.freedesktop.portal.Desktop</c> is
/// allowed by default in every Flatpak.
///
/// **Why the one-step call.** The interface has two: <c>PreparePrint</c>, which
/// returns page setup and a token so an app can lay pages out itself, and
/// <c>Print</c>, which takes a document that is already laid out. MegaPDF hands over
/// a finished PDF — the live document, unsaved edits included, written to a temp
/// file by the caller — so <c>Print</c> without a token is the whole conversation,
/// and the portal shows the print dialog itself. That is also why the app's own
/// printer dialog is skipped in the sandbox: two dialogs asking the same question
/// is worse than one.
///
/// **What this file talks.** Raw D-Bus, through <c>Tmds.DBus.Protocol</c> — which
/// is not a new dependency: Avalonia's own FreeDesktop integration already brings
/// it, and the reference is pinned here so it is a choice rather than an accident.
/// The conversation is:
///
/// <code>
///   -> org.freedesktop.portal.Print.Print(parent_window, title, fd, {handle_token})
///   &lt;- (o) the request's object path
///   &lt;- org.freedesktop.portal.Request.Response(u response, a{sv} results)
///         0 = printed, 1 = the person cancelled, 2 = it went wrong
/// </code>
///
/// The match on the Response signal is added <em>before</em> the call, because the
/// portal may answer before the method reply is read; the request path is the one
/// the spec says to predict, from this connection's unique name and the token.
/// </summary>
/// <summary>How far a hand-over got. Only <see cref="Answered"/> means somebody
/// used the dialog; <see cref="Accepted"/> means everything the app is
/// responsible for worked and the dialog is still open.</summary>
internal enum PortalStage
{
    NoPortal,
    Refused,
    Accepted,
    Answered,
}

[SupportedOSPlatform("linux")]
internal static class PortalPrinter
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string PrintInterface = "org.freedesktop.portal.Print";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>
    /// The version the desktop's Print portal reports, or null if there is no
    /// portal, no session bus, or no Print interface on it.
    ///
    /// Asked for rather than assumed: a desktop can run a portal whose backend
    /// implements FileChooser and not Print, and the answer decides whether the
    /// app offers printing at all or says why it cannot.
    /// </summary>
    internal static async Task<uint?> VersionAsync(TimeSpan timeout)
    {
        try
        {
            using var connection = await ConnectAsync(timeout).ConfigureAwait(false);
            if (connection is null)
                return null;

            using var cancel = new CancellationTokenSource(timeout);
            return await connection
                .CallMethodAsync(VersionRequest(connection),
                                 (Message m, object? _) =>
                                     m.GetBodyReader().ReadVariantValue().GetUInt32(),
                                 null)
                .WaitAsync(cancel.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hands a PDF to the desktop to print. Returns when the portal has answered —
    /// which is after the person has used its dialog, so the timeout is generous
    /// and is for a portal that never answers, not for someone deciding.
    /// </summary>
    /// <param name="pdfPath">The document to print. The caller wrote it; the fd is opened read-only.</param>
    /// <param name="jobTitle">What the dialog and the queue call the job.</param>
    /// <param name="timeout">How long to wait for the portal's answer.</param>
    internal static async Task<Printing.Outcome> PrintAsync(
        string pdfPath, string jobTitle, TimeSpan timeout)
        => (await HandOverAsync(pdfPath, jobTitle, timeout).ConfigureAwait(false)).Outcome;

    /// <inheritdoc cref="PrintAsync"/>
    /// <remarks>
    /// The same conversation, reporting how far it got as well as what to show.
    /// `--portal-print-check` uses the stage: unattended, the dialog is never
    /// answered, so "the portal took the file descriptor and opened its dialog"
    /// is the pass and a timeout is not a failure.
    /// </remarks>
    internal static async Task<(Printing.Outcome Outcome, PortalStage Stage)> HandOverAsync(
        string pdfPath, string jobTitle, TimeSpan timeout)
    {
        Connection? connection = null;
        var stage = PortalStage.NoPortal;
        try
        {
            connection = await ConnectAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (connection is null || connection.UniqueName is not { } unique)
                return (new Printing.Outcome(false, Strings.PrintingNeedsPortal),
                        PortalStage.NoPortal);

            // The token is ours to choose and must be a valid object-path element:
            // letters, digits and underscores only.
            var token = "megapdf_" + Guid.NewGuid().ToString("N");
            var requestPath = RequestPath(unique, token);

            var answered = new TaskCompletionSource<uint>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Subscribe first: the portal is allowed to answer before the method
            // reply arrives, and a Response that lands with nobody listening is a
            // print that appears to hang.
            using var match = await connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    Path = requestPath,
                    Interface = RequestInterface,
                    Member = "Response",
                },
                (Message m, object? _) => m.GetBodyReader().ReadUInt32(),
                (Exception? error, uint response, object? _, object? _) =>
                {
                    if (error is not null)
                        answered.TrySetException(error);
                    else
                        answered.TrySetResult(response);
                },
                ObserverFlags.None, null, null).ConfigureAwait(false);

            // The descriptor is duplicated by the transport when the message is
            // sent, so closing ours afterwards is right.
            using var handle = File.OpenHandle(pdfPath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read);

            using var callTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            stage = PortalStage.Refused;
            await connection
                .CallMethodAsync(PrintRequest(connection, handle, jobTitle, token))
                .WaitAsync(callTimeout.Token)
                .ConfigureAwait(false);
            // The portal took the descriptor and has a request open. Everything
            // this app is responsible for has worked by here.
            stage = PortalStage.Accepted;

            using var waiting = new CancellationTokenSource(timeout);
            var response = await answered.Task.WaitAsync(waiting.Token).ConfigureAwait(false);
            stage = PortalStage.Answered;
            return (response switch
            {
                0 => new Printing.Outcome(true, Strings.SentToPrinter),
                1 => new Printing.Outcome(false, Strings.PrintingCancelled),
                _ => new Printing.Outcome(false, Strings.CouldNotPrint),
            }, stage);
        }
        catch (OperationCanceledException)
        {
            return (new Printing.Outcome(
                false, Strings.WithDetail(Strings.CouldNotPrint, Strings.PrintQueueDidNotAnswer)),
                stage);
        }
        catch (Exception ex)
        {
            // A portal that is not there, a bus that is not there, a backend that
            // does not implement Print: all of them arrive here, and the detail is
            // more use than a sentence of ours that says printing failed.
            return (new Printing.Outcome(
                false, Strings.WithDetail(Strings.PrintingNeedsPortal, FirstLine(ex.Message))),
                stage);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    // MessageWriter is a ref struct and cannot live across an await, so each
    // message is built by a plain method that hands back the buffer.

    private static MessageBuffer VersionRequest(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: PortalPath,
            @interface: PropertiesInterface, member: "Get", signature: "ss",
            flags: MessageFlags.None);
        writer.WriteString(PrintInterface);
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private static MessageBuffer PrintRequest(
        Connection connection, SafeHandle document, string jobTitle, string token)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: PortalPath,
            @interface: PrintInterface, member: "Print", signature: "ssha{sv}",
            flags: MessageFlags.None);
        writer.WriteString(ParentWindow());
        writer.WriteString(string.IsNullOrWhiteSpace(jobTitle) ? "MegaPDF" : jobTitle);
        writer.WriteHandle(document);
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }

    /// <summary>
    /// The object path the portal will use for this request, predicted the way the
    /// spec says to: the sender's unique name with its leading colon dropped and
    /// its dots turned into underscores, then the token.
    /// </summary>
    /// <remarks>
    /// Separated from the conversation so it can be tested without a bus — it is
    /// the one piece of this file that is pure string work, and getting it wrong
    /// means listening on a path nothing ever sends to.
    /// </remarks>
    internal static string RequestPath(string uniqueName, string token)
    {
        var sender = uniqueName.StartsWith(':') ? uniqueName[1..] : uniqueName;
        sender = sender.Replace('.', '_');
        return $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
    }

    /// <summary>
    /// How the app identifies its window to the portal, so the dialog can be
    /// parented. Empty is allowed and means "no parent"; X11 wants
    /// <c>x11:&lt;hex window id&gt;</c>, which Avalonia does not hand out through any
    /// public API, so the dialog opens unparented. On a single-window app that
    /// costs focus ordering and nothing else.
    /// </summary>
    private static string ParentWindow() => string.Empty;

    private static async Task<Connection?> ConnectAsync(TimeSpan timeout)
    {
        var address = Address.Session;
        if (string.IsNullOrEmpty(address))
            return null;
        var connection = new Connection(address);
        using var cancel = new CancellationTokenSource(timeout);
        await connection.ConnectAsync().AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
        return connection;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? text : line;
    }
}
