using System.Formats.Tar;
using System.Globalization;

namespace AllInOneInstaller.Remote;

/// <summary>A folder of the All-in-1 downloader and where its files go in /var/www/gamedata.</summary>
/// <param name="Source">Relative to the downloader's folder, with '/'.</param>
/// <param name="Target">Relative to /var/www/gamedata.</param>
/// <param name="Only">File names to take. Null takes every file matching <paramref name="Pattern"/>.</param>
/// <param name="AddOnly">Only add missing files and leave existing ones alone unless asked.</param>
public sealed record AssetMapping(string Name, string Source, string Target, string Pattern, IReadOnlyList<string>? Only = null, bool AddOnly = false);

public sealed record LocalFile(string Name, string Path, long Size, long Modified);

/// <summary>Files one folder needs to upload. A file counts as changed when its size differs or the local copy is newer.</summary>
public sealed record AssetPlan(AssetMapping Mapping, string SourcePath, int LocalCount, long LocalBytes, IReadOnlyList<LocalFile> New, IReadOnlyList<LocalFile> Changed)
{
    public IEnumerable<LocalFile> Upload => New.Concat(Changed);
    public long UploadBytes => Upload.Sum(f => f.Size);
    public int UploadCount => New.Count + Changed.Count;
}

/// <summary>
/// Copies the assets the All-in-1 downloader (github.com/duckietm/all-in-1-converter) put in its Habbo_Default folder
/// to /var/www/gamedata, in the layout Octane's configuration uses. Only new and changed files are sent, as one tar
/// stream per folder over the SSH session; nothing on the server is deleted.
/// </summary>
public static class AssetSync
{
    public const string DefaultFolder = "Habbo_Default";

    /// <summary>Config files Octane loads from gamedata/config.</summary>
    public static readonly string[] ConfigFiles =
        ["FurnitureData.json", "ProductData.json", "FigureData.json", "FigureMap.json", "EffectMap.json", "HabboAvatarActions.json", "ExternalTexts.json"];

    public static readonly AssetMapping[] Mappings =
    [
        new("Furniture", "Habbo_Default/hof_furni/hab", "furniture", "*.hab"),
        new("Furniture icons", "Habbo_Default/hof_furni/icons", "icons", "*.png"),
        new("Clothes", "Habbo_Default/clothes/hab", "clothes", "*.hab"),
        new("Effects", "Habbo_Default/effects/hab", "effect", "*.hab"),
        new("Pets", "Habbo_Default/pets/hab", "pets", "*.hab"),
        new("Badges", "Habbo_Default/badges", "c_images/album1584", "*"),
        new("Catalogue icons", "Habbo_Default/icons", "c_images/catalogue", "*.png"),
        new("Effect icons", "Habbo_Default/fx_icons", "c_images/fx_icons", "*.png"),
        new("Quest images", "Habbo_Default/quests", "c_images/Quests", "*"),
        new("Hotel view", "Habbo_Default/reception", "c_images/reception", "*"),
        new("Promo images", "Habbo_Default/reception/web_promo_small", "c_images/web_promo_small", "*"),
        new("Sounds", "Habbo_Default/mp3", "sounds", "*.mp3"),
        // Only added when missing, because the emulator edits ExternalTexts.json for custom badges and hotels edit the others.
        new("Game data (config JSON)", "Habbo_Default/files/json", "config", "*.json", ConfigFiles, AddOnly: true)
    ];

    /// <summary>The folder holding Habbo_Default for the downloader chosen in Settings, or null.</summary>
    public static string? DownloaderFolder(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string folder = Directory.Exists(executable) ? executable : Path.GetDirectoryName(executable) ?? "";
        return Directory.Exists(Path.Combine(folder, DefaultFolder)) ? folder : null;
    }

    private static List<LocalFile> Scan(string folder, AssetMapping mapping)
    {
        if (!Directory.Exists(folder)) return [];
        var files = new List<LocalFile>();
        foreach (string path in Directory.EnumerateFiles(folder, mapping.Pattern, SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (mapping.Only is { } only && !only.Contains(name, StringComparer.Ordinal)) continue;
            // Skip names that would not survive tar or a shell.
            if (name.StartsWith('.') || name.Any(c => char.IsControl(c) || c is '/' or '\\')) continue;
            var info = new FileInfo(path);
            files.Add(new LocalFile(name, path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
        }
        return files;
    }

    /// <summary>Compares every folder with the server using a single listing command.</summary>
    public static async Task<List<AssetPlan>> PlanAsync(ServerConnection connection, string downloaderFolder, bool overwriteConfig,
        CancellationToken token = default)
    {
        string script = string.Join('\n', Mappings.Select((m, i) =>
            $"echo '== {i}'; find {ServerConnection.ShellQuote($"{OctaneInstaller.Gamedata}/{m.Target}")} -maxdepth 1 -type f -printf '%f\\t%s\\t%T@\\n' 2>/dev/null"))
            + "\ntrue";
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var remote = new Dictionary<int, Dictionary<string, (long Size, double Modified)>>();
        Dictionary<string, (long, double)>? current = null;
        foreach (string line in result.Output.Split('\n'))
        {
            if (line.StartsWith("== ", StringComparison.Ordinal))
            {
                remote[int.Parse(line[3..].Trim(), CultureInfo.InvariantCulture)] = current = new Dictionary<string, (long, double)>(StringComparer.Ordinal);
                continue;
            }
            string[] f = line.Split('\t');
            if (current is null || f.Length < 3) continue;
            current[f[0]] = (long.TryParse(f[1], out long size) ? size : -1,
                double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double modified) ? modified : 0);
        }

        var plans = new List<AssetPlan>();
        for (int i = 0; i < Mappings.Length; i++)
        {
            AssetMapping mapping = Mappings[i];
            string source = Path.Combine([downloaderFolder, .. mapping.Source.Split('/')]);
            List<LocalFile> local = Scan(source, mapping);
            Dictionary<string, (long Size, double Modified)> there = remote.GetValueOrDefault(i) ?? new();
            var fresh = new List<LocalFile>();
            var changed = new List<LocalFile>();
            foreach (LocalFile file in local)
            {
                if (!there.TryGetValue(file.Name, out var server)) fresh.Add(file);
                else if ((!mapping.AddOnly || overwriteConfig) && (server.Size != file.Size || file.Modified > server.Modified + 2)) changed.Add(file);
            }
            plans.Add(new AssetPlan(mapping, source, local.Count, local.Sum(f => f.Size), fresh, changed));
        }
        return plans;
    }

    /// <summary>Free bytes on the file system holding /var/www/gamedata, or /var/www if that does not exist yet.</summary>
    public static async Task<long> FreeSpaceAsync(ServerConnection connection, CancellationToken token = default)
    {
        string output = (await connection.RunAsRootAsync(
            $"d={OctaneInstaller.Gamedata}; [ -d \"$d\" ] || d=/var/www; df -P -B1 \"$d\" | awk 'NR == 2 {{ print $4 }}'", token)).Output.Trim();
        return long.TryParse(output, out long free) ? free : long.MaxValue;
    }

    /// <summary>Streams the plan's files as tar into the target folder. <paramref name="progress"/> gets the bytes sent so far.</summary>
    public static async Task UploadAsync(ServerConnection connection, AssetPlan plan, Action<long> progress, CancellationToken token = default)
    {
        string target = ServerConnection.ShellQuote($"{OctaneInstaller.Gamedata}/{plan.Mapping.Target}");
        string script = $$"""
            set -e
            install -d -m 2775 -o www-data -g www-data {{target}}
            # Keep file dates for the next sync to compare. Owner and mode come from the server.
            tar -x -f - -C {{target}} --no-same-owner --no-same-permissions
            echo "OK $(find {{target}} -maxdepth 1 -type f | wc -l)"
            """;
        LocalFile[] files = plan.Upload.ToArray();
        CommandResult result = await connection.RunAsRootWithInputAsync(script, async (stdin, ct) =>
        {
            long sent = 0;
            await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
            foreach (LocalFile file in files)
            {
                ct.ThrowIfCancellationRequested();
                await tar.WriteEntryAsync(file.Path, file.Name, ct);
                sent += file.Size;
                progress(sent);
            }
        }, token);
        if (!result.Succeeded || !result.Output.Contains("OK ", StringComparison.Ordinal))
            throw new ServerConnectionException($"Unpacking {plan.Mapping.Name} on the server failed: {(result.Output + result.Error).Trim()}");
    }

    /// <summary>Runs the gamedata-permissions script from the Octane step, or a plain chgrp when it is missing.</summary>
    public static Task FixPermissionsAsync(ServerConnection connection, Action<string> log, CancellationToken token = default) =>
        RemoteScript.RunAsync(connection, RemoteScript.Prelude + $$"""
            if [ -x {{OctaneInstaller.PermissionsScript}} ]; then
              echo "> {{OctaneInstaller.PermissionsScript}}"
              {{OctaneInstaller.PermissionsScript}}
            else
              echo "> chgrp -R www-data {{OctaneInstaller.Gamedata}} (run the Octane step for the full permissions script)"
              chgrp -R www-data {{OctaneInstaller.Gamedata}}
              chmod -R g+rwX,o+rX {{OctaneInstaller.Gamedata}}
            fi
            """, log, "Setting the permissions failed", token);
}
