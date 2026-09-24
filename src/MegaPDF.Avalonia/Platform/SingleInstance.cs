using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Threading;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Linux single-instance (#348 phase 2 Part B): a second <c>megapdf &lt;path&gt;</c>
/// launch hands its <c>.pdf</c> command-line arguments to the process already running
/// instead of starting a second one with its own window. Windows gets the same result
/// through <c>AppInstance</c> redirection (#350); macOS never needed it — LaunchServices
/// always talks to the one running app, which is the bug Part A of this phase fixes for
/// more than one handed-over file. Linux has neither, so this is the app's own mechanism.
///
/// <b>Mechanism.</b> A Unix domain socket at <c>$XDG_RUNTIME_DIR/megapdf.sock</c>, falling
/// back to <see cref="UserDataPaths.InAppFolder(string[])"/>'s <c>instance.sock</c> when
/// <c>$XDG_RUNTIME_DIR</c> is unset. <c>System.Net.Sockets.UnixDomainSocketEndPoint</c> —
/// no package beyond what the BCL already provides. Chosen over D-Bus activation
/// (<c>org.freedesktop.Application</c>, <c>DBusActivatable=true</c>) because the latter
/// needs the <c>.desktop</c> file renamed to match the bus name — a separate change per
/// the #348 plan's own §3b, not bundled into this one.
///
/// <c>$XDG_RUNTIME_DIR</c> is per-app-id inside a Flatpak sandbox
/// (<c>$XDG_RUNTIME_DIR/app/&lt;id&gt;</c>, shared by every instance of the same app) and
/// is equally private and per-app inside a Snap sandbox, so the socket lands somewhere
/// every instance of *this* app — and only this app — can reach, in both.
///
/// <b>Protocol.</b> The client connects, writes one absolute path per line (UTF-8, "\n"
/// line endings), shuts its send side down (a half-close, so the server's line reader
/// sees a clean end of input without needing a length prefix), then reads one byte back
/// — an ack that proves the running instance actually received the paths before this
/// process exits and, with it, whatever launched it stops waiting. Zero paths is a valid
/// message: a bare <c>megapdf</c> with no file still redirects, the same as every other
/// single-instance desktop app, and the app brings its window to the front for it
/// (App.axaml.cs) rather than opening nothing and doing nothing.
///
/// <b>What this class does not decide.</b> It never touches a <see cref="Avalonia.Controls.Window"/>
/// or a view model — <see cref="RoutePaths"/> is set from App.axaml.cs once a window
/// exists, and everything this class receives is handed to that callback, posted onto
/// <see cref="Dispatcher.UIThread"/> — but only once <see cref="MarkDispatcherRunning"/>
/// confirms the dispatcher's own main loop is actually pumping (see its doc for why
/// that gate exists at all: a real, reproducible crash, not a defensive nicety).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class SingleInstance
{
    private const string SocketFileName = "megapdf.sock";

    private static Socket? _listener;
    private static string? _boundPath;

    private static readonly object RouteLock = new();
    private static readonly List<string> Buffered = [];
    private static Action<IReadOnlyList<string>>? _routePaths;
    private static bool _dispatcherRunning;

    /// <summary>
    /// Where a redirected launch's paths land once the app is ready for them — set
    /// from App.axaml.cs once the first window exists. A connection accepted before
    /// this is set (a narrow window right at startup) is buffered and delivered the
    /// moment it is, rather than dropped.
    /// </summary>
    internal static Action<IReadOnlyList<string>>? RoutePaths
    {
        get => _routePaths;
        set
        {
            lock (RouteLock)
            {
                _routePaths = value;
            }
            FlushIfReady();
        }
    }

    /// <summary>
    /// Marks Avalonia's own dispatcher loop as genuinely running. Everything this
    /// class delivers before this is called is buffered rather than posted to
    /// <see cref="Dispatcher.UIThread"/>, and nothing is ever dropped.
    ///
    /// This exists because of a real, reproducible crash found while building this
    /// feature, not as a defensive nicety. <see cref="TryListen"/> starts accepting
    /// connections in <c>Program.Main</c>, before <c>BuildAvaloniaApp</c> is even
    /// called — so a connection can be accepted, read, and reach <see cref="Deliver"/>
    /// (which used to call <c>Dispatcher.UIThread.Post</c> unconditionally) while the
    /// accept loop's background continuation is running well ahead of the main
    /// thread's own progress through Avalonia's startup, which can easily still be
    /// tens to hundreds of milliseconds from actually starting <c>Dispatcher.MainLoop</c>.
    /// When that race was lost, <c>Dispatcher.MainLoop</c> itself threw
    /// <c>PlatformNotSupportedException</c> the moment it did start — reproduced on
    /// demand in roughly one launch in three during this feature's development, on a
    /// real second process over the real socket, both under WSLg's forwarded X11 and
    /// under a plain Xvfb (so not a WSLg-only quirk, as an earlier pass at this fix
    /// mistakenly concluded from too small a sample); never reproduced by posting
    /// from an unrelated background timer with no socket involved, which is the
    /// asymmetry that pointed at the ordering rather than at background-thread
    /// `Post` calls being unsafe in general. Ten-for-ten clean runs (both display
    /// backends) followed once this gate went in — see
    /// <c>tools/linux/check-single-instance.sh</c>.
    ///
    /// Only a <c>Post</c> made after <c>MainLoop</c> is confirmed running is safe.
    /// App.axaml.cs establishes that the one way that is actually certain: it posts
    /// this very call to <see cref="Dispatcher.UIThread"/> from the UI thread itself,
    /// during startup. A <c>Post</c> made *from* the UI thread *to* the UI thread is
    /// always safe — Avalonia apps do this constantly — and the posted job running at
    /// all is the proof that <c>MainLoop</c> is genuinely pumping its queue, which is
    /// the fact this method needs to be sure of before this class' own background
    /// continuation is allowed to make that same call.
    /// </summary>
    internal static void MarkDispatcherRunning()
    {
        lock (RouteLock)
        {
            _dispatcherRunning = true;
        }
        FlushIfReady();
    }

    private static void FlushIfReady()
    {
        Action<IReadOnlyList<string>>? route;
        List<string>? toDeliver = null;
        lock (RouteLock)
        {
            route = _routePaths;
            if (_dispatcherRunning && route is not null && Buffered.Count > 0)
            {
                toDeliver = [.. Buffered];
                Buffered.Clear();
            }
        }
        if (toDeliver is not null)
            route!(toDeliver);
    }

    private static string SocketPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
            return Path.Combine(runtimeDir, SocketFileName);

        // UserDataUnavailableException (an IOException) if even this has nowhere to
        // go — left to the caller, which already treats "the socket could not be
        // reached/created" as "run without single-instancing this launch."
        return UserDataPaths.InAppFolder(SocketFileName);
    }

    /// <summary>
    /// The one entry point Program.Main calls, before <c>BuildAvaloniaApp</c>, with
    /// the absolute paths of every <c>.pdf</c> argument on this launch's command line
    /// (possibly none). True means this process handed them to an already-running
    /// instance and <c>Main</c> must return at once, without ever building the app.
    /// False means this process is — or, after trying to become it here, now is —
    /// the instance later launches will redirect to.
    ///
    /// Never throws: every failure (no runtime directory, a read-only one, a socket
    /// that will not bind) falls back to "this launch runs standalone, the same as
    /// before this feature existed" rather than stopping the app from starting.
    /// </summary>
    internal static bool TryRedirectOrBecomePrimary(IReadOnlyList<string> pdfPaths)
    {
        if (TryRedirect(pdfPaths))
            return true;

        if (TryListen())
            return false;

        // Lost a startup race — two cold launches close enough together that both
        // failed to connect before either had bound — or some other reason this
        // process could not become the listener. One more attempt to redirect, in
        // case a concurrent launch just finished becoming primary; if that fails
        // too, this launch simply runs without single-instancing.
        Thread.Sleep(50);
        return TryRedirect(pdfPaths);
    }

    /// <summary>
    /// Tries to hand <paramref name="paths"/> to an already-running instance and get
    /// its ack. False for every reason that means "nothing is listening" — a missing
    /// socket file, a stale one nobody answers on (<c>ECONNREFUSED</c>), or the
    /// runtime directory itself being unreachable.
    /// </summary>
    private static bool TryRedirect(IReadOnlyList<string> paths)
    {
        Socket? client = null;
        try
        {
            var path = SocketPath();
            client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));

            using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    NewLine = "\n",
                };
                foreach (var p in paths)
                    writer.WriteLine(p);
                writer.Flush();
            }

            // Half-close: lets the server's line reader see a clean end of input
            // without a length-prefixed protocol. The NetworkStream above was told
            // not to own the socket, so it is still open for this and for the read
            // below.
            client.Shutdown(SocketShutdown.Send);

            var ack = new byte[1];
            var acked = client.Receive(ack) > 0;
            if (acked)
                Console.WriteLine($"single-instance: handed {paths.Count} path(s) to the already-running instance");
            return acked;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Binds the socket — unlinking a stale file a previous instance that did not
    /// exit cleanly left behind first, since <see cref="TryRedirect"/> having just
    /// failed already proves nothing answers on it — and accepts connections for the
    /// rest of the process's life.
    ///
    /// Fully asynchronous (<see cref="Socket.AcceptAsync()"/>/thread-pool), not a
    /// dedicated blocking OS thread calling the synchronous <c>Accept()</c> — the
    /// managed thread pool is what the rest of the app, Dispatcher included, already
    /// runs on everywhere else, so socket I/O stays on the same footing rather than
    /// introducing a bare <see cref="Thread"/> nothing else in the process uses. This
    /// change alone did not fix the startup race <see cref="MarkDispatcherRunning"/>
    /// documents and actually resolves — see that method for the full account.
    /// </summary>
    private static bool TryListen()
    {
        try
        {
            var path = SocketPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            TryUnlink(path);

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(16);

            _listener = listener;
            _boundPath = path;

            _ = AcceptLoopAsync(listener);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopListening();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"::warning::single-instance socket could not start ({ex.GetType().Name}: {ex.Message}); "
                + "a later `megapdf <path>` on this machine will start its own process instead of joining this one.");
            return false;
        }
    }

    /// <summary>
    /// Unlinks the socket file on a clean shutdown, so the next launch never has to
    /// treat this process's own socket as stale. Safe to call more than once.
    /// </summary>
    internal static void StopListening()
    {
        try
        {
            _listener?.Close();
        }
        catch (Exception)
        {
        }
        _listener = null;

        if (_boundPath is { } path)
            TryUnlink(path);
        _boundPath = null;
    }

    private static void TryUnlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task AcceptLoopAsync(Socket listener)
    {
        while (true)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The listener was closed (StopListening) or the socket is gone —
                // either way this instance is shutting down; nothing to retry.
                return;
            }

            try
            {
                await HandleConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A malformed or dropped connection must not take the listener down
                // — every later `megapdf <path>` launch on this machine depends on
                // this loop staying up.
            }
        }
    }

    private static async Task HandleConnectionAsync(Socket connection)
    {
        using (connection)
        {
            var paths = new List<string>();
            using (var stream = new NetworkStream(connection, ownsSocket: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                                                 bufferSize: -1, leaveOpen: true))
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (line.Length > 0)
                        paths.Add(line);
                }
            }

            Console.WriteLine($"single-instance: received {paths.Count} path(s) from a redirected launch");
            Deliver(paths);

            // The ack: proves to the client that these paths were actually read
            // before it exits. The byte's value carries no meaning of its own.
            await connection.SendAsync(new byte[] { 0 }, SocketFlags.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hands paths to the app — never straight to <see cref="RoutePaths"/> from this
    /// accept thread/task, which is not one Avalonia's bindings or view models expect
    /// to run on. Buffered, never posted to <see cref="Dispatcher.UIThread"/>, until
    /// <see cref="MarkDispatcherRunning"/> has actually been called: see that
    /// method's doc for the crash that guard exists to avoid. Once the dispatcher is
    /// confirmed running, posting from here is exactly as safe as it is everywhere
    /// else in the app that hands work to the UI thread from a background task.
    /// </summary>
    private static void Deliver(IReadOnlyList<string> paths)
    {
        bool ready;
        lock (RouteLock)
        {
            ready = _dispatcherRunning;
            if (!ready)
            {
                Buffered.AddRange(paths);
                return;
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            Action<IReadOnlyList<string>>? route;
            lock (RouteLock)
            {
                route = _routePaths;
                if (route is null)
                {
                    Buffered.AddRange(paths);
                    return;
                }
            }
            route(paths);
        });
    }

    /// <summary>
    /// For the self-test: simulates a connection having already been read and
    /// parsed, without a real second process or socket — the delivery and buffering
    /// logic is exercised the same way a real connection drives it, through the same
    /// UI-thread post <see cref="Deliver"/> makes once the dispatcher is running.
    /// </summary>
    internal static void DeliverForTest(IReadOnlyList<string> paths) => Deliver(paths);

    /// <summary>For the self-test: each scenario is its own simulated process, and every static below is process-wide.</summary>
    internal static void ResetForTest()
    {
        lock (RouteLock)
        {
            _routePaths = null;
            _dispatcherRunning = false;
            Buffered.Clear();
        }
    }
}
