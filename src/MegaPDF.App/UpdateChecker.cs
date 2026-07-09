using System.Net.Http;
using System.Text.Json;
using MegaPDF.Core.Services;
using Windows.Management.Deployment;

namespace MegaPDF.App;

/// <summary>
/// The end-user update experience: a quiet startup check against GitHub releases;
/// if a newer version exists, a gentle bar offers one-click update. The new package
/// downloads and STAGES while the app keeps running (deferred registration), then
/// applies on the next launch — or immediately via "Restart now".
/// Only active in the packaged (installed) app; dev builds skip it entirely.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/SlyWombat/MegaPDF/releases/latest";

    private string? _msixDownloadUrl;

    public string? AvailableVersion { get; private set; }

    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Returns the newer version's display string, or null. Never throws — a
    /// failed check (offline, rate limit) must not bother the user at startup.</summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            if (!IsPackaged)
                return null;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaPDF-UpdateCheck");
            http.Timeout = TimeSpan.FromSeconds(10);
            using var response = await http.GetAsync(LatestReleaseApi);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = json.RootElement.GetProperty("tag_name").GetString();

            var packaged = Windows.ApplicationModel.Package.Current.Id.Version;
            var current = new Version(packaged.Major, packaged.Minor, packaged.Build, packaged.Revision);
            if (!UpdateVersion.IsNewer(tag, current))
                return null;

            foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                {
                    _msixDownloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (_msixDownloadUrl is null)
                return null;

            AvailableVersion = tag!.TrimStart('v', 'V');
            return AvailableVersion;
        }
        catch
        {
            return null; // update checks are best-effort, always silent on failure
        }
    }

    /// <summary>Downloads the release package and stages it; it activates on next launch.</summary>
    public async Task DownloadAndStageAsync()
    {
        if (_msixDownloadUrl is null)
            throw new InvalidOperationException("No update available.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"MegaPDF-update-{AvailableVersion}.msix");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaPDF-UpdateCheck");
            using var download = await http.GetStreamAsync(_msixDownloadUrl);
            using var file = File.Create(tempPath);
            await download.CopyToAsync(file);
        }

        var manager = new PackageManager();
        var options = new AddPackageOptions
        {
            // The whole point: install while the app is running; Windows swaps
            // the registration in on the next launch.
            DeferRegistrationWhenPackagesAreInUse = true,
        };
        var result = await manager.AddPackageByUriAsync(new Uri(tempPath), options);
        if (!result.IsRegistered && result.ExtendedErrorCode is not null && result.ErrorText?.Length > 0)
            throw new InvalidOperationException(result.ErrorText);
    }

    /// <summary>Relaunches into the updated version.</summary>
    public static void RestartNow() =>
        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
}
