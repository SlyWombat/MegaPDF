namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// What the platform printers hand back and forth (SDD §3.5). Plain types with no
/// platform of their own, so the view model and the print dialog can name them
/// without being macOS or Linux code themselves — which is what the platform
/// compatibility analyser correctly complains about otherwise.
/// </summary>
internal static class Printing
{
    /// <summary>What a probe or a print attempt found, in words a status bar can show.</summary>
    internal sealed record Outcome(bool Ok, string Message);

    /// <summary>
    /// A printer to send to: its queue name, what it calls itself, and whether the
    /// system considers it the default. macOS gets its destinations from the print
    /// panel and never builds these; Linux does (#158).
    /// </summary>
    internal sealed record Destination(string Name, string? Description, bool IsDefault)
    {
        /// <summary>What the picker shows: the description if there is one, else the queue name.</summary>
        internal string Label => string.IsNullOrWhiteSpace(Description) ? Name : Description!;
    }

    /// <summary>What the person chose: a queue (null for the system default) and how many copies.</summary>
    internal sealed record Choice(string? Destination, int Copies);
}
