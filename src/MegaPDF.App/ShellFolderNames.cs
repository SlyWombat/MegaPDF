using System.Runtime.InteropServices;
using MegaPDF.Core.Services;

namespace MegaPDF.App;

/// <summary>
/// The folders File Explorer names for the user, with the names it uses (#165): the
/// profile folder, the known folders under it, and OneDrive. The names come from the
/// shell, so they follow the Windows display language ("Téléchargements") and any
/// renamed or redirected folder, exactly as Explorer's address bar shows them.
/// </summary>
internal static class ShellFolderNames
{
    private static IReadOnlyList<NamedFolder>? _cached;

    public static IReadOnlyList<NamedFolder> Get() => _cached ??= Load();

    private static List<NamedFolder> Load()
    {
        var paths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            KnownFolderPath(DownloadsFolderId),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        };

        var folders = new List<NamedFolder>();
        foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path))
                continue;
            folders.Add(new NamedFolder(path, DisplayName(path)));
        }
        return folders;
    }

    /// <summary>The shell's display name for a folder, or its own name if the shell has none.</summary>
    private static string DisplayName(string path)
    {
        var info = new SHFILEINFOW();
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), ShgfiDisplayName) != IntPtr.Zero
            && !string.IsNullOrWhiteSpace(info.szDisplayName))
        {
            return info.szDisplayName;
        }
        return Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
    }

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0)
            return "";
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private const uint ShgfiDisplayName = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
