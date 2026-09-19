using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Printing on Linux (SDD §3.5, #158), through CUPS — the printing system every
/// target distribution runs — by handing the document to <c>lp</c>.
///
/// **Why lp and not a toolkit print dialog.** Avalonia has no print API, so the
/// alternatives were GTK's print dialog through P/Invoke or the XDG desktop
/// portal's <c>org.freedesktop.portal.Print</c>. GTK would mean linking a second
/// toolkit into an app that already has one, for a dialog; the portal is the right
/// answer inside a Flatpak sandbox and needs a D-Bus conversation with a file
/// descriptor hand-off that nothing outside a sandbox benefits from. <c>lp</c> is
/// what both of those eventually call, it is present wherever CUPS is, and it is a
/// process boundary rather than an ABI — a wrong argument is an error message, not
/// a segfault. MegaPDF asks for the destination itself (Views/PrinterWindow), which
/// is the part <c>lp</c> alone would otherwise skip.
///
/// **Flatpak.** A sandboxed build has no <c>lp</c> and goes through
/// <see cref="PortalPrinter"/> instead — the desktop's own print dialog, over
/// D-Bus, with a file descriptor rather than a path (#158). This file stays the
/// CUPS route and says so when it is asked to print inside a sandbox, where the
/// caller should have taken the portal branch.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxPrinter
{
    // A destination, a choice and an outcome are Printing's, not this file's: the
    // view model and the print dialog name all three, and neither is Linux code.

    /// <summary>True inside a Flatpak sandbox, where lp is not reachable and the portal is the route.</summary>
    internal static bool InFlatpakSandbox => File.Exists("/.flatpak-info");

    /// <summary>
    /// True inside the snap. The snap asks for no <c>cups</c> plug and stages no
    /// <c>lp</c>, for the same reason the Flatpak has no CUPS socket: the portal
    /// prints for it, and xdg-desktop-portal serves a strictly confined snap exactly
    /// as it serves a Flatpak (tools/linux/snap/snapcraft.yaml). <c>SNAP_NAME</c> is
    /// set by snapd for every command a snap runs, and by nothing else.
    /// </summary>
    internal static bool InSnapSandbox => Environment.GetEnvironmentVariable("SNAP_NAME") is { Length: > 0 };

    /// <summary>Inside either sandbox: no lp, and the print portal is the route.</summary>
    internal static bool InSandbox => InFlatpakSandbox || InSnapSandbox;

    /// <summary>"Flatpak" or "snap", for reports that say which sandbox they found.</summary>
    internal static string SandboxName => InFlatpakSandbox ? "Flatpak" : "snap";

    /// <summary>Whether CUPS' client tools are installed at all.</summary>
    internal static bool IsAvailable => !InSandbox && ResolveOnPath("lp") is not null;

    /// <summary>
    /// The destinations CUPS knows about, most-preferred first (the default leads).
    /// Empty when there is no CUPS server, no queue, or no lpstat — all of which are
    /// states of the machine rather than faults, so none of them throws.
    /// </summary>
    internal static IReadOnlyList<Printing.Destination> Destinations()
    {
        if (ResolveOnPath("lpstat") is not { } lpstat)
            return [];

        // -p lists the queues with their state, -d names the system default. One
        // invocation, because two would race a queue being added between them.
        var listed = Run(lpstat, ["-p", "-d"], TimeSpan.FromSeconds(10));
        return ParseDestinations(listed.Output + listed.Error);
    }

    /// <summary>
    /// Reads `lpstat -p -d` output. Separated from the process call so its parsing is
    /// checkable on a machine with no printers — which is every CI runner.
    /// </summary>
    /// <remarks>
    /// The two lines it looks for, in CUPS' own wording:
    /// <code>
    /// printer Office_Laser is idle.  enabled since Tue 16 Sep 2026 09:12:04 EDT
    /// system default destination: Office_Laser
    /// </code>
    /// Anything else — "no system default destination", the per-queue status lines
    /// that follow an indent, a localised trailer — is ignored rather than guessed at.
    /// </remarks>
    internal static IReadOnlyList<Printing.Destination> ParseDestinations(string lpstatOutput)
    {
        var names = new List<string>();
        string? defaultName = null;

        foreach (var raw in lpstatOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Continuation lines for a queue's state are indented; only the queue
            // lines themselves start at the margin.
            if (line.StartsWith("printer ", StringComparison.Ordinal))
            {
                var name = line["printer ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }
            else if (line.StartsWith("system default destination:", StringComparison.Ordinal))
            {
                var name = line["system default destination:".Length..].Trim();
                if (name.Length > 0)
                    defaultName = name;
            }
        }

        // A default that has no queue line of its own is still a destination: CUPS
        // prints it for an implicit class or a remote queue it has not polled yet.
        if (defaultName is not null && !names.Contains(defaultName, StringComparer.Ordinal))
            names.Insert(0, defaultName);

        return [.. names
            .Select(n => new Printing.Destination(n, DescriptionOf(n), string.Equals(n, defaultName, StringComparison.Ordinal)))
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// A queue's human name ("HP LaserJet in the hall"), from its CUPS description.
    /// Best effort: a queue with none is shown by its queue name, which is what
    /// every other Linux print dialog does.
    /// </summary>
    private static string? DescriptionOf(string queue)
    {
        if (ResolveOnPath("lpstat") is not { } lpstat)
            return null;
        var listed = Run(lpstat, ["-l", "-p", queue], TimeSpan.FromSeconds(10));
        foreach (var raw in listed.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Description:", StringComparison.Ordinal))
            {
                var text = line["Description:".Length..].Trim();
                return text.Length == 0 ? null : text;
            }
        }
        return null;
    }

    /// <summary>
    /// Sends a PDF already on disk to a CUPS destination. The caller has written the
    /// live document — unsaved edits included — to that file.
    /// </summary>
    /// <param name="pdfPath">The document to print.</param>
    /// <param name="destination">A queue name from <see cref="Destinations"/>, or null for the system default.</param>
    /// <param name="jobTitle">What the print queue calls the job — the document's name.</param>
    /// <param name="copies">How many. One or more.</param>
    internal static Printing.Outcome Print(string pdfPath, string? destination, string jobTitle, int copies)
    {
        if (!OperatingSystem.IsLinux())
            return new Printing.Outcome(false, Strings.PrintingUnavailableHere);

        if (InSandbox)
            return new Printing.Outcome(false, Strings.PrintingNeedsPortal);

        if (ResolveOnPath("lp") is not { } lp)
            return new Printing.Outcome(false, Strings.PrintingNeedsCups);

        // Every value below is an argv element, never a shell word, so a queue name
        // or a document title with a space, a quote or a newline in it is data. The
        // "--" is what keeps a path that begins with "-" from being read as a flag.
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(destination))
        {
            arguments.Add("-d");
            arguments.Add(destination!);
        }
        arguments.Add("-t");
        arguments.Add(string.IsNullOrWhiteSpace(jobTitle) ? "MegaPDF" : jobTitle);
        if (copies > 1)
        {
            arguments.Add("-n");
            arguments.Add(copies.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.Add("--");
        arguments.Add(pdfPath);

        // lp reads the file itself and returns once the job is queued, so this does
        // not wait for paper. Two minutes is for a spooler that is wedged, not for a
        // big document.
        var run = Run(lp, arguments, TimeSpan.FromMinutes(2));
        if (run.TimedOut)
            return new Printing.Outcome(false, Strings.WithDetail(Strings.CouldNotPrint, Strings.PrintQueueDidNotAnswer));

        if (run.ExitCode != 0)
        {
            // CUPS puts the reason on stderr — "lp: Error - scheduler not responding",
            // "lp: Error - The printer or class does not exist." Showing it is far
            // more use than a message of ours that says printing failed.
            var detail = FirstLine(run.Error) ?? FirstLine(run.Output) ?? $"lp exited {run.ExitCode}";
            return new Printing.Outcome(false, Strings.WithDetail(Strings.CouldNotPrint, detail));
        }

        return new Printing.Outcome(true, Strings.SentToPrinter);
    }

    /// <summary>
    /// Verifies the whole printing route without printing: that CUPS' tools are
    /// there, that the destination parsing is right, and that the argv a print would
    /// build is well formed.
    ///
    /// The parsing check is the one that matters, and it runs against a fixed sample
    /// rather than the machine's own queues — a CI runner has no printers, so a probe
    /// that only asked the machine would pass by finding nothing and prove nothing.
    /// </summary>
    internal static Printing.Outcome Probe()
    {
        if (!OperatingSystem.IsLinux())
            return new Printing.Outcome(false, "not Linux");

        var report = new StringBuilder();

        // A sample in CUPS' own wording, with the traps: an indented continuation
        // line, a default that leads, and a queue whose name contains the word the
        // parser looks for.
        const string sample =
            "printer Office_Laser is idle.  enabled since Tue 16 Sep 2026 09:12:04 EDT\n" +
            "\tready to print\n" +
            "printer printer_room is idle.  enabled since Tue 16 Sep 2026 09:12:04 EDT\n" +
            "system default destination: printer_room\n";

        var parsed = ParseDestinations(sample);
        var names = parsed.Select(d => d.Name).ToArray();
        if (names.Length != 2 || !names.Contains("Office_Laser") || !names.Contains("printer_room"))
            return new Printing.Outcome(false, $"lpstat parsing found [{string.Join(", ", names)}], expected the two queues");
        if (parsed[0].Name != "printer_room" || !parsed[0].IsDefault)
            return new Printing.Outcome(false, $"the system default did not lead the list (got {parsed[0].Name}, default={parsed[0].IsDefault})");
        if (parsed.Count(d => d.IsDefault) != 1)
            return new Printing.Outcome(false, "more than one destination claimed to be the default");
        report.Append("lpstat parsing: the default leads and both queues are found");

        if (InSandbox)
        {
            // Inside the sandbox the CUPS route is not the route, so what is worth
            // reporting is whether the desktop offers the portal that is: asked
            // for, not assumed, because a backend can implement FileChooser and
            // not Print.
            var version = PortalPrinter.VersionAsync(TimeSpan.FromSeconds(5))
                                       .GetAwaiter().GetResult();
            report.Append(version is null
                ? $"; in a {SandboxName} sandbox, and this session offers no "
                  + "org.freedesktop.portal.Print — printing has no route here"
                : $"; in a {SandboxName} sandbox, printing through "
                  + $"org.freedesktop.portal.Print version {version}");
            return new Printing.Outcome(version is not null, report.ToString());
        }

        // Outside the sandbox the portal is not needed, but whether it is there is
        // worth knowing: it is the same desktop the Flatpak build will meet.
        var outside = PortalPrinter.VersionAsync(TimeSpan.FromSeconds(5))
                                   .GetAwaiter().GetResult();
        report.Append(outside is null
            ? "; no print portal on this session (not needed outside a sandbox)"
            : $"; org.freedesktop.portal.Print version {outside} is also available");

        if (ResolveOnPath("lp") is null)
            return new Printing.Outcome(false, "lp is not on PATH — install the CUPS client tools (cups-client)");
        report.Append("; lp and lpstat resolve");

        // What the machine itself has. Reported, never asserted: a runner with no
        // printers is not a fault.
        var here = Destinations();
        report.Append(here.Count == 0
            ? "; this machine has no CUPS destinations (expected on a build runner)"
            : $"; this machine has {here.Count} CUPS destination(s)");

        return new Printing.Outcome(true, report.ToString());
    }

    // --- Running a program, without a shell ---------------------------------

    private readonly record struct Run_(int ExitCode, string Output, string Error, bool TimedOut);

    /// <summary>
    /// The absolute path of a program on PATH, or null. Resolved rather than left to
    /// the process launcher so "is CUPS installed" is answerable without running
    /// anything, and so nothing is ever looked up in the working directory.
    /// </summary>
    private static string? ResolveOnPath(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, program);
            }
            catch (ArgumentException)
            {
                continue; // a PATH entry with invalid characters in it
            }
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static Run_ Run(string program, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return new Run_(-1, "", $"could not start {program}", false);

            // lp reads stdin when it is given no file; closing it means a mistake in
            // the argument list fails rather than hangs waiting for a document.
            process.StandardInput.Close();

            // Read both pipes before waiting: a program that fills one while we wait
            // on exit deadlocks, and lpstat -l is chatty enough to do it.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return new Run_(-1, "", "", true);
            }

            return new Run_(process.ExitCode, output.Result ?? "", error.Result ?? "", false);
        }
        catch (Exception ex)
        {
            return new Run_(-1, "", ex.Message, false);
        }
    }

    private static string? FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                return line;
        }
        return null;
    }
}
