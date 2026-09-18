namespace MegaPDF.Core.Services;

/// <summary>A folder the platform has a display name for: a known folder, a drive, a home directory.</summary>
/// <param name="Path">Its full path, however the platform spells it.</param>
/// <param name="DisplayName">What the platform calls it, in the platform's language ("Téléchargements").</param>
public sealed record NamedFolder(string Path, string DisplayName);

/// <summary>
/// Where a recent document lives, as a line to show under its name (#165).
///
/// Recent lists show the file name, and files from one template or one scanner share
/// it: six rows reading "blank-agreement.pdf" tell nobody which is which. Every row
/// gets its folder, in the platform's own words — known folders by their display
/// name, the user's profile as their account folder, a drive by its letter — joined
/// with "›" the way File Explorer's and Finder's path bars do.
///
/// The path formatting lives here, away from any UI, because both desktops need it
/// and because the interesting parts are worth testing: which folder identifies a
/// row, and what to drop when the line is too long.
/// </summary>
public static class RecentLocation
{
    public const string Separator = " › ";

    /// <summary>The folder segments of <paramref name="filePath"/>, outermost first.</summary>
    /// <param name="named">
    /// Folders with a platform display name, longest path first is not required — the
    /// longest match wins. The matched folder becomes the first segment, and nothing
    /// above it is shown: a file in Documents\Clients reads "Documents › Clients".
    /// </param>
    /// <param name="opaque">
    /// Roots whose insides must never be spelled out — a sandbox container, an app's
    /// private storage. A file under one reads as its own folder alone, which is all
    /// the platform is willing to say about it, rather than as the route through the
    /// container: "fixtures", not "claude › … › Data › tmp › fixtures" (#146 §3).
    /// </param>
    public static IReadOnlyList<string> Segments(string filePath, IReadOnlyList<NamedFolder> named,
                                                 IReadOnlyList<string>? opaque = null)
    {
        // String work only, no System.IO: a Windows path must format the same when the
        // tests run on the Mac, and a Mac path the same on Windows.
        var cut = filePath.LastIndexOfAny(['\\', '/']);
        if (cut <= 0)
            return [];
        var folder = filePath[..cut];

        // Checked before the named folders, because a container lives inside the home
        // folder and would otherwise match it and print the whole way down.
        if (opaque is not null && opaque.Any(root => IsSameOrBelow(folder, root)))
        {
            var own = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return own is { Length: > 0 } ? [own] : [];
        }

        var match = named
            .Where(f => IsSameOrBelow(folder, f.Path))
            .OrderByDescending(f => f.Path.TrimEnd('\\', '/').Length)
            .FirstOrDefault();

        var segments = new List<string>();
        string rest;
        if (match is not null)
        {
            segments.Add(match.DisplayName);
            rest = folder[Math.Min(match.Path.TrimEnd('\\', '/').Length, folder.Length)..];
        }
        else if (folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\... reads as "server › share › ...".
            rest = folder;
        }
        else if (folder.Length >= 2 && folder[1] == ':' && char.IsAsciiLetter(folder[0]))
        {
            segments.Add(folder[..2].ToUpperInvariant());
            rest = folder[2..];
        }
        else
        {
            rest = folder;
        }

        segments.AddRange(rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
        return segments;
    }

    /// <summary>
    /// The location line for a row: the segments joined, shortened in the middle when it
    /// is longer than <paramref name="maxLength"/>.
    ///
    /// The first segment says which place this is ("Documents", "D:"), and the last says
    /// which folder — the part that tells two rows with the same file name apart. So the
    /// middle is what goes, and <paramref name="keepDeepest"/> pins any further segment
    /// that has to stay, which is what the caller works out when two rows would otherwise
    /// read the same.
    /// </summary>
    public static string Line(IReadOnlyList<string> segments, int maxLength = 48, int keepDeepest = 0)
    {
        if (segments.Count == 0)
            return "";
        var line = string.Join(Separator, segments);
        if (line.Length <= maxLength || segments.Count <= 2)
            return line;

        // Drop middle segments, shallowest first, until it fits. The first segment and the
        // tail — the parent folder plus keepDeepest more — always stay, even if the line
        // is then still long: a clipped end is better than a row that reads like another.
        var firstPinned = Math.Max(1, segments.Count - 1 - keepDeepest);
        var chosen = Enumerable.Range(0, segments.Count).ToList();
        while (true)
        {
            var candidate = Join(segments, chosen);
            if (candidate.Length <= maxLength)
                return candidate;
            var dropIndex = chosen.FindIndex(i => i != 0 && i < firstPinned);
            if (dropIndex < 0)
                return candidate;
            chosen.RemoveAt(dropIndex);
        }
    }

    /// <summary>
    /// How many folders above the parent a row needs before it reads differently from the
    /// others with the same file name. 0 when the parent folder already tells them apart.
    /// </summary>
    public static int DistinguishingDepth(IReadOnlyList<IReadOnlyList<string>> segmentsOfEachRow)
    {
        var rows = segmentsOfEachRow.Where(s => s.Count > 0).ToList();
        if (rows.Count < 2)
            return 0;
        var deepest = rows.Max(r => r.Count);
        for (var depth = 0; depth < deepest; depth++)
        {
            var tails = rows.Select(r => string.Join(Separator, r.TakeLast(depth + 1))).ToList();
            if (tails.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == tails.Count)
                return depth;
        }
        return deepest - 1;
    }

    private static string Join(IReadOnlyList<string> segments, List<int> chosen)
    {
        var parts = new List<string>();
        for (var i = 0; i < chosen.Count; i++)
        {
            if (i > 0 && chosen[i] != chosen[i - 1] + 1)
                parts.Add("…");
            parts.Add(segments[chosen[i]]);
        }
        return string.Join(Separator, parts);
    }

    private static bool IsSameOrBelow(string folder, string ancestor)
    {
        var a = folder.TrimEnd('\\', '/');
        var b = ancestor.TrimEnd('\\', '/');
        if (b.Length == 0)
            return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
               || (a.Length > b.Length && a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                   && (a[b.Length] == '\\' || a[b.Length] == '/'));
    }
}
