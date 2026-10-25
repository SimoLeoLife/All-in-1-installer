using System.Formats.Tar;

namespace AllInOneInstaller.Remote;

/// <summary>A folder of Assets/, found in the Polaris clone and next to the installer, and where it goes in /var/www/gamedata.</summary>
/// <param name="Source">Relative to Assets/, with '/'.</param>
/// <param name="Target">Relative to /var/www/gamedata.</param>
public sealed record EmulatorAssetFolder(string Name, string Source, string Target, string Pattern, bool Recursive, string Purpose);

/// <summary>Files one folder misses, split into those the server's Polaris clone has and those only the installer has.</summary>
public sealed record EmulatorAssetState(EmulatorAssetFolder Folder, int Total, IReadOnlyList<string> FromServer, IReadOnlyList<string> FromInstaller)
{
    public int Missing => FromServer.Count + FromInstaller.Count;
}

/// <summary>
/// Client images and group badge parts that the Habbo downloads lack. Polaris ships them in Assets/ of its repository
/// and the installer carries a copy next to its executable. Octane preloads images/loading_icon.png, clear_icon.png and
/// big_arrow.png. If one is missing the client fails to start and logs in with an empty SSO ticket, which the emulator
/// logs as "invalid SSO ticket". Missing files are copied from the server's clone first and the rest are uploaded.
/// Existing files are never replaced.
/// </summary>
public static class EmulatorAssetsInstaller
{
    private static readonly EmulatorAssetFolder[] Fixed =
    [
        new("Client images", "images", "images", "*", true,
            "loading_icon / clear_icon / big_arrow (Octane preloads them), wallet, navigator and catalogue extras"),
        new("Hotel view", "images/reception", "c_images/reception", "*.png", false,
            "the hotel view's background, sun, drape and sides (ui-config's c_images/reception)"),
        new("Group badge parts", "badgeparts", "c_images/groupbadge/badgeparts", "badgepart_*.png", false,
            "the parts of group badges, for Octane and the emulator's badge imager")
    ];

    /// <summary>
    /// The fixed folders, then every other top-level folder in the installer's Assets/, mapped to the same path in
    /// gamedata. Adding a folder there is enough to have it uploaded.
    /// </summary>
    public static IReadOnlyList<EmulatorAssetFolder> Folders
    {
        get
        {
            var folders = new List<EmulatorAssetFolder>(Fixed);
            if (!Directory.Exists(LocalAssets)) return folders;
            HashSet<string> taken = [.. Fixed.Select(f => f.Source.Split('/')[0])];
            foreach (string directory in Directory.EnumerateDirectories(LocalAssets).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(directory);
                if (taken.Contains(name) || name.StartsWith('.')) continue;
                string[] parts = Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
                folders.Add(new EmulatorAssetFolder(name == "c_images" ? "Game images (c_images)" : name, name, name, "*", true,
                    parts.Length > 0 ? string.Join(", ", parts) : $"Assets/{name} → gamedata/{name}"));
            }
            return folders;
        }
    }

    public static string ServerAssets => EmulatorInstaller.Repository + "/Assets";
    public static string LocalAssets => Path.Combine(AppContext.BaseDirectory, "Assets");

    private static string ServerSource(EmulatorAssetFolder f) => $"{ServerAssets}/{f.Source}";
    private static string Target(EmulatorAssetFolder f) => $"{OctaneInstaller.Gamedata}/{f.Target}";
    private static string LocalSource(EmulatorAssetFolder f) => Path.Combine([LocalAssets, .. f.Source.Split('/')]);

    private static string Find(string directory, EmulatorAssetFolder f, bool sourceFilter) =>
        $"[ -d {ServerConnection.ShellQuote(directory)} ] && (cd {ServerConnection.ShellQuote(directory)} && find . " +
        (f.Recursive ? "" : "-maxdepth 1 ") + "-type f " +
        (sourceFilter ? $"-name {ServerConnection.ShellQuote(f.Pattern)} ! -iname 'README*' " : "") + "-printf '%P\\n')";

    private static bool Usable(string name) => name.Length > 0 && !name.Split('/').Any(p => p.StartsWith('.')) && !name.Any(char.IsControl);

    private static List<string> LocalFiles(EmulatorAssetFolder f)
    {
        string root = LocalSource(f);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, f.Pattern, f.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(name => Usable(name) && !Path.GetFileName(name).StartsWith("README", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<List<EmulatorAssetState>> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        IReadOnlyList<EmulatorAssetFolder> folders = Folders;
        string script = string.Join('\n', folders.Select((f, i) =>
            $"echo '== S{i}'; {Find(ServerSource(f), f, true)}\necho '== T{i}'; {Find(Target(f), f, false)}")) + "\ntrue";
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var lists = new Dictionary<string, HashSet<string>>();
        HashSet<string>? current = null;
        foreach (string line in result.Output.Split('\n'))
        {
            string text = line.TrimEnd('\r');
            if (text.StartsWith("== ", StringComparison.Ordinal)) lists[text[3..]] = current = new HashSet<string>(StringComparer.Ordinal);
            else if (current is not null && Usable(text)) current.Add(text);
        }

        var states = new List<EmulatorAssetState>();
        for (int i = 0; i < folders.Count; i++)
        {
            HashSet<string> server = lists.GetValueOrDefault($"S{i}") ?? [];
            HashSet<string> target = lists.GetValueOrDefault($"T{i}") ?? [];
            HashSet<string> local = [.. LocalFiles(folders[i])];
            var all = new HashSet<string>(server, StringComparer.Ordinal);
            all.UnionWith(local);
            states.Add(new EmulatorAssetState(folders[i], all.Count,
                server.Where(n => !target.Contains(n)).Order(StringComparer.Ordinal).ToList(),
                local.Where(n => !server.Contains(n) && !target.Contains(n)).Order(StringComparer.Ordinal).ToList()));
        }
        return states;
    }

    /// <summary>Adds the missing files without replacing any and returns how many were added.</summary>
    public static async Task<int> InstallAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        List<EmulatorAssetState> states = await InspectAsync(connection, token);
        int added = 0;

        // Copy what the server's Polaris clone has, so those files need no upload.
        string copies = string.Join('\n', states.Where(s => s.FromServer.Count > 0).Select(s => $$"""
            src={{ServerConnection.ShellQuote(ServerSource(s.Folder))}}
            dst={{ServerConnection.ShellQuote(Target(s.Folder))}}
            install -d -m 2775 -o www-data -g www-data "$dst"
            n=0
            while IFS= read -r f; do
              [ -n "$f" ] || continue
              if [ ! -e "$dst/$f" ]; then
                install -d -m 2775 -o www-data -g www-data "$(dirname "$dst/$f")"
                install -m 664 -o www-data -g www-data "$src/$f" "$dst/$f"
                n=$((n + 1))
              fi
            done <<'ALL_IN_1_FILES'
            {{string.Join('\n', s.FromServer)}}
            ALL_IN_1_FILES
            echo "  {{s.Folder.Name}}: $n copied from $src to $dst"
            echo "ADDED=$n"
            """));
        if (copies.Length > 0)
        {
            int copied = 0;
            await RemoteScript.RunAsync(connection, RemoteScript.Prelude + copies, line =>
            {
                if (line.Trim().StartsWith("ADDED=", StringComparison.Ordinal)) copied += int.TryParse(line.Trim()[6..], out int n) ? n : 0;
                else log(line);
            }, "Copying the emulator's assets failed", token);
            added += copied;
        }

        // Upload the rest, including everything when there is no clone, from Assets/ next to the installer.
        foreach (EmulatorAssetState state in states.Where(s => s.FromInstaller.Count > 0))
        {
            string target = ServerConnection.ShellQuote(Target(state.Folder));
            string script = $$"""
                set -e
                install -d -m 2775 -o www-data -g www-data {{target}}
                # --skip-old-files keeps files that are already there.
                tar -x -f - -C {{target}} --no-same-owner --no-same-permissions --skip-old-files
                find {{target}} -user root -exec chown www-data:www-data {} +
                echo OK
                """;
            string root = LocalSource(state.Folder);
            CommandResult result = await connection.RunAsRootWithInputAsync(script, async (stdin, ct) =>
            {
                await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
                foreach (string name in state.FromInstaller)
                    await tar.WriteEntryAsync(Path.Combine([root, .. name.Split('/')]), name, ct);
            }, token);
            if (!result.Succeeded || !result.Output.Contains("OK", StringComparison.Ordinal))
                throw new ServerConnectionException($"Uploading {state.Folder.Name.ToLowerInvariant()} failed: {(result.Output + result.Error).Trim()}");
            log($"    {state.Folder.Name}: {state.FromInstaller.Count} uploaded from the installer to {Target(state.Folder)}");
            added += state.FromInstaller.Count;
        }

        if (added > 0)
            await RemoteScript.RunAsync(connection,
                $"if [ -x {OctaneInstaller.PermissionsScript} ]; then {OctaneInstaller.PermissionsScript} >/dev/null; fi",
                log, "Setting the permissions failed", token);
        if (added == 0) log("    Nothing missing.");
        return added;
    }
}
