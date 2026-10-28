using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LightingControl;

/// <summary>
/// Checks the latest GitHub release on every launch and, if it is newer than the running build, offers to
/// replace the exe in place. The release workflow attaches <c>LightingControl-&lt;tag&gt;-win-x64.exe</c>.
/// </summary>
internal static class Updater
{
    private const string Repository = "plastina/lightning-control";
    private const string AssetSuffix = "-win-x64.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LightingControl-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>Only a published single-file exe can replace itself; `dotnet run` / debug builds must not.</summary>
    private static bool CanSelfUpdate =>
        ExePath is { } path
        && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        && !Repository.StartsWith("OWNER/", StringComparison.Ordinal);

    /// <summary>Removes the previous exe that was renamed out of the way by the last update.</summary>
    public static void CleanUp()
    {
        try
        {
            if (ExePath is { } path && File.Exists(path + ".old"))
                File.Delete(path + ".old");
        }
        catch { /* still locked by the old process shutting down; next launch will get it */ }
    }

    /// <summary>Fire-and-forget at startup. Never throws: a failed check must not get in the way of the app.</summary>
    public static async Task CheckAndPromptAsync()
    {
        if (!CanSelfUpdate)
            return;
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var latest))
                return;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (Normalize(latest) <= Normalize(current))
                return;

            string? downloadUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name is not null && name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (downloadUrl is null)
                return;

            var answer = System.Windows.MessageBox.Show(
                $"A new version is available: {tag} (you have {Normalize(current)}).\n\nUpdate now? The app will restart.",
                "Lighting Control update",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;

            await DownloadAndRestartAsync(downloadUrl);
        }
        catch
        {
            // Offline, rate-limited, malformed response...: skip silently, we will try again next launch.
        }
    }

    private static async Task DownloadAndRestartAsync(string url)
    {
        var exe = ExePath!;
        var staged = exe + ".new";
        var old = exe + ".old";

        try
        {
            await using (var source = await Http.GetStreamAsync(url))
            await using (var target = File.Create(staged))
                await source.CopyToAsync(target);

            // A running exe can be renamed but not overwritten: move it aside, then drop the new one in its place.
            if (File.Exists(old))
                File.Delete(old);
            File.Move(exe, old);
            File.Move(staged, exe);
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch { }
            System.Windows.MessageBox.Show($"The update could not be installed:\n{ex.Message}",
                "Lighting Control update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    // Assembly versions are always 4 parts (1.2.3.0) while tags are usually 3: compare them on equal footing.
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
