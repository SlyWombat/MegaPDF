using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.UI.StartScreen;

namespace MegaPDF.App;

/// <summary>
/// The taskbar icon's Recent list (#165). Windows builds it itself from the documents
/// an app records as recently used, and describes each entry with its full path — which
/// is exactly what tells two files of the same name apart in a jump list.
///
/// Packaged builds only: the most-recently-used list and JumpList need package identity,
/// and the dev build has none. Failures are ignored on purpose; a jump list is a
/// convenience, and nothing in the app depends on it.
/// </summary>
internal static class JumpListRecents
{
    private static bool _groupEnabled;

    public static async Task RecordAsync(string path)
    {
        try
        {
            if (!JumpList.IsSupported())
                return;
            var file = await StorageFile.GetFileFromPathAsync(path);
            StorageApplicationPermissions.MostRecentlyUsedList.Add(file, path, RecentStorageItemVisibility.AppAndSystem);
            if (_groupEnabled)
                return;
            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.Recent;
            await list.SaveAsync();
            _groupEnabled = true;
        }
        catch (Exception)
        {
            // No identity, no permission, a file on a device that has gone: the app is
            // unaffected either way.
        }
    }
}
