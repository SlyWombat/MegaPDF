namespace MegaPDF.Core.Services;

/// <summary>Release-tag version comparison for the startup update check.</summary>
public static class UpdateVersion
{
    /// <summary>Parses tags like "v1.4.0" or "1.4.0.0".</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed))
            return false;
        version = Normalize(parsed);
        return true;
    }

    public static bool IsNewer(string? tag, Version current) =>
        TryParseTag(tag, out var tagged) && tagged > Normalize(current);

    /// <summary>Missing build/revision compare as 0 (v1.4 == 1.4.0.0).</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
