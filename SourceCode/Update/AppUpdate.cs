using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AllInOneInstaller.Update;

/// <summary>A release on GitHub that is newer than the running program.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, string Notes, string PageUrl, string AssetName, string AssetUrl, long AssetBytes);

/// <summary>
/// Keeps the program itself up to date. At startup it asks GitHub for the newest release of
/// <see cref="Repository"/>; when that release is newer than the running build, the download replaces the
/// running file and the program starts again. Only published releases count: a repository without releases
/// never asks anything.
/// </summary>
public static class AppUpdate
{
    public const string Repository = "duckietm/All-in-1-installer";

    private static readonly string LatestRelease = $"https://api.github.com/repos/{Repository}/releases/latest";

    // Renamed out of the way while the new file takes its place; removed at the next start.
    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    /// <summary>The version of the running build, from the assembly.</summary>
    public static Version Current
    {
        get
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            // InformationalVersion can carry "+<commit>" from the build; only the numbers matter here.
            string? text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (text is { Length: > 0 } && Version.TryParse(text.Split('+', '-')[0], out Version? informational)) return informational;
            return assembly.GetName().Version ?? new Version(0, 0, 0);
        }
    }

    /// <summary>The file of the running program: the single-file executable, not the .dll next to it.</summary>
    public static string? ExecutablePath =>
        Environment.ProcessPath is { Length: > 0 } path && !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>
    /// True for the published program: one self-contained file that can replace itself. A build started
    /// through dotnet, or one with its DLLs next to it, is left alone. A single-file build has no .dll of
    /// its own on disk, because everything is inside the one file.
    /// </summary>
    public static bool CanUpdate => ExecutablePath is { } exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));

    /// <summary>Removes what an earlier update left behind. Safe to call at every start.</summary>
    public static void CleanUp()
    {
        if (ExecutablePath is not { } exe) return;
        foreach (string leftover in new[] { exe + OldSuffix, exe + NewSuffix })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use or not ours to delete: the next start tries again.
            }
        }
    }

    /// <summary>
    /// The newest release when it is newer than the running build and has a file for this operating system,
    /// or null. Never throws: no internet, no releases or a GitHub that answers something else all mean
    /// "nothing to update".
    /// </summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken token = default)
    {
        if (!CanUpdate) return null;
        try
        {
            using HttpClient client = Client();
            using HttpResponseMessage response = await client.GetAsync(LatestRelease, token);
            // 404 is the normal answer for a repository that has no releases yet.
            if (!response.IsSuccessStatusCode) return null;

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            JsonElement release = document.RootElement;
            if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()) return null;
            if (release.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean()) return null;

            string tag = Text(release, "tag_name") ?? "";
            if (ParseVersion(tag) is not { } version || version <= Current) return null;
            if (Asset(release) is not { } asset) return null;

            return new AvailableUpdate(version, tag, Text(release, "body") ?? "", Text(release, "html_url") ?? "",
                asset.Name, asset.Url, asset.Bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the release file next to the running one, moves the running one aside and puts the new one
    /// in its place, keeping its name. Throws when the folder cannot be written.
    /// </summary>
    public static async Task DownloadAndReplaceAsync(AvailableUpdate update, IProgress<(long Done, long Total)>? progress,
        CancellationToken token = default)
    {
        string exe = ExecutablePath ?? throw new InvalidOperationException(
            "The update only works on the published program, not on a build started through dotnet.");
        string downloaded = exe + NewSuffix;
        string moved = exe + OldSuffix;

        using (HttpClient client = Client())
        {
            using HttpResponseMessage response = await client.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.AssetBytes;

            await using Stream source = await response.Content.ReadAsStreamAsync(token);
            await using (FileStream target = File.Create(downloaded))
            {
                byte[] buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    progress?.Report((done, total));
                }
            }
            // A half-finished download must never replace a working program.
            if (total > 0 && new FileInfo(downloaded).Length != total)
            {
                File.Delete(downloaded);
                throw new IOException($"The download stopped early ({new FileInfo(downloaded).Length} of {total} bytes).");
            }
        }

        if (File.Exists(moved)) File.Delete(moved);
        // A running program cannot be overwritten, but it can be renamed.
        File.Move(exe, moved);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(moved, exe);
            throw;
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Starts the replaced program and leaves the old one; the caller closes the window right after.</summary>
    public static void Restart()
    {
        if (ExecutablePath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
    }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        // The GitHub API refuses requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("All-in-1-Installer", Current.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // "v1.2.0", "1.2.0" and "release-1.2" all become a version; anything else is ignored.
    private static Version? ParseVersion(string tag)
    {
        string digits = new(tag.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return digits.Contains('.') && Version.TryParse(digits, out Version? version) ? version : null;
    }

    // The single-file build for this operating system: "...win-x64.exe" or the Linux file next to it.
    private static (string Name, string Url, long Bytes)? Asset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) return null;
        bool windows = OperatingSystem.IsWindows();
        string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        (string Name, string Url, long Bytes)? fallback = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = Text(asset, "name") ?? "";
            string url = Text(asset, "browser_download_url") ?? "";
            if (url.Length == 0) continue;
            long bytes = asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0;

            bool forThisSystem = windows
                ? name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                : name.Contains("linux", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (!forThisSystem) continue;
            if (name.Contains(architecture, StringComparison.OrdinalIgnoreCase)) return (name, url, bytes);
            fallback ??= (name, url, bytes);
        }
        return fallback;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
