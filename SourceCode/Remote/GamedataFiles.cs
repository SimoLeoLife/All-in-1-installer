using System.Formats.Tar;
using System.Globalization;

namespace AllInOneInstaller.Remote;

public sealed record RemoteEntry(string Name, bool IsDirectory, bool IsLink, long Size, DateTime Modified);

/// <summary>
/// The Asset Explorer's server side, limited to /var/www/gamedata. Paths are checked twice. Here, names may not
/// contain ".." or separators. In every script, realpath follows symlinks and must stay inside gamedata, so neither a
/// crafted name nor a symlink reaches anything else. Uploads get owner www-data, folders 2775 and files 664.
/// </summary>
public static class GamedataFiles
{
    public const string Root = OctaneInstaller.Gamedata;

    // "inside PATH" prints the real path, or fails when it resolves outside gamedata, symlinks included.
    private const string Guard = $$"""
        set -e
        # Called in $(...), so the message goes to stderr and set -e stops the script on failure.
        inside() {
          r=$(realpath -m -- "$1") || { echo "ERROR: invalid path" >&2; exit 3; }
          case "$r" in
            {{Root}}|{{Root}}/*) printf '%s' "$r" ;;
            *) echo "ERROR: that path is outside {{Root}}" >&2; exit 3 ;;
          esac
        }
        # The web server and the emulator, both in group www-data, may read and change uploaded files.
        fix_rights() {
          chown -R www-data:www-data -- "$1"
          find "$1" -type d -exec chmod 2775 {} +
          find "$1" -type f -exec chmod 664 {} +
        }

        """;

    /// <summary>Maps "a/b" to "/var/www/gamedata/a/b" and throws for anything that would leave gamedata.</summary>
    public static string Absolute(string relative)
    {
        var parts = new List<string>();
        foreach (string part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") throw new ArgumentException("Paths outside gamedata are not allowed.");
            parts.Add(part);
        }
        return parts.Count == 0 ? Root : Root + "/" + string.Join('/', parts);
    }

    /// <summary>Returns why a single file or folder name is not allowed, or null when it is fine.</summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name is "." or "..") return "That name is not allowed.";
        if (name.IndexOfAny(['/', '\\']) >= 0) return "A name cannot contain / or \\.";
        if (name.Any(char.IsControl)) return "A name cannot contain control characters.";
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 255) return "That name is too long.";
        return null;
    }

    private static string Q(string text) => ServerConnection.ShellQuote(text);

    private static void CheckNames(params IEnumerable<string> names)
    {
        foreach (string name in names)
            if (NameProblem(name) is { } problem) throw new ArgumentException($"{problem} ({name})");
    }

    // Prefers the script's own "ERROR: ..." line over everything it printed.
    private static string ErrorOf(CommandResult result) =>
        (result.Output + "\n" + result.Error).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR: ", StringComparison.Ordinal))?[7..]
        ?? (result.Output + result.Error).Trim();

    private static async Task<CommandResult> RunAsync(ServerConnection connection, string script, string failure, CancellationToken token)
    {
        CommandResult result = await connection.RunAsRootAsync(Guard + script, token);
        if (!result.Succeeded)
        {
            throw new ServerConnectionException($"{failure}: {ErrorOf(result)}");
        }
        return result;
    }

    public static async Task<List<RemoteEntry>> ListAsync(ServerConnection connection, string relative, CancellationToken token = default)
    {
        CommandResult result = await RunAsync(connection, $$"""
            d=$(inside {{Q(Absolute(relative))}})
            [ -d "$d" ] || { echo "ERROR: not a folder"; exit 3; }
            cd "$d" && find . -mindepth 1 -maxdepth 1 -printf '%y\t%s\t%T@\t%f\n'
            """, "Reading the folder failed", token);
        var entries = new List<RemoteEntry>();
        foreach (string line in result.Output.Split('\n'))
        {
            string[] f = line.Split('\t', 4);
            if (f.Length < 4 || f[3].Length == 0 || f[3].Any(char.IsControl)) continue;
            long.TryParse(f[1], out long size);
            double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
            entries.Add(new RemoteEntry(f[3], f[0] == "d", f[0] == "l", size,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime));
        }
        return entries;
    }

    public static Task CreateFolderAsync(ServerConnection connection, string relative, string name, CancellationToken token = default)
    {
        CheckNames(name);
        return RunAsync(connection, $$"""
            p=$(inside {{Q(Absolute(relative) + "/" + name)}})
            [ ! -e "$p" ] || { echo "ERROR: "{{Q(name)}}" already exists"; exit 3; }
            install -d -m 2775 -o www-data -g www-data "$p"
            """, "Creating the folder failed", token);
    }

    public static Task RenameAsync(ServerConnection connection, string relative, string from, string to, CancellationToken token = default)
    {
        CheckNames(from, to);
        return RunAsync(connection, $$"""
            a=$(inside {{Q(Absolute(relative) + "/" + from)}})
            b=$(inside {{Q(Absolute(relative) + "/" + to)}})
            [ "$a" != "{{Root}}" ] || { echo "ERROR: gamedata itself cannot be renamed"; exit 3; }
            [ ! -e "$b" ] || { echo "ERROR: "{{Q(to)}}" already exists"; exit 3; }
            mv -- "$a" "$b"
            """, "Renaming failed", token);
    }

    public static Task DeleteAsync(ServerConnection connection, string relative, IReadOnlyList<string> names, CancellationToken token = default)
    {
        CheckNames(names);
        return RunAsync(connection, string.Join('\n', names.Select(name => $$"""
            p=$(inside {{Q(Absolute(relative) + "/" + name)}})
            [ "$p" != "{{Root}}" ] || { echo "ERROR: gamedata itself cannot be deleted"; exit 3; }
            rm -rf -- "$p"
            """)), "Deleting failed", token);
    }

    /// <summary>The files and their sizes under the given entries of a folder, recursing into folders, for a download.</summary>
    public static async Task<List<(string Path, long Size)>> FilesUnderAsync(ServerConnection connection, string relative,
        IReadOnlyList<string> names, CancellationToken token = default)
    {
        CheckNames(names);
        CommandResult result = await RunAsync(connection, $$"""
            d=$(inside {{Q(Absolute(relative))}})
            cd "$d"
            {{string.Join('\n', names.Select(n => $"p=$(inside {Q(Absolute(relative) + "/" + n)}); find {Q("./" + n)} -type f -printf '%s\\t%p\\n'"))}}
            """, "Reading the files failed", token);
        var files = new List<(string, long)>();
        foreach (string line in result.Output.Split('\n'))
        {
            string[] f = line.Split('\t', 2);
            if (f.Length == 2 && long.TryParse(f[0], out long size) && !f[1].Any(char.IsControl) && !f[1].Split('/').Contains(".."))
                files.Add((f[1].StartsWith("./", StringComparison.Ordinal) ? f[1][2..] : f[1], size));
        }
        return files;
    }

    /// <summary>
    /// Uploads local files and folders into a gamedata folder as one tar stream. Existing files are replaced unless
    /// <paramref name="skipExisting"/> is set. Afterwards every uploaded item gets the gamedata rights.
    /// </summary>
    public static async Task UploadAsync(ServerConnection connection, IReadOnlyList<string> localPaths, string relative, bool skipExisting,
        Action<long> progress, CancellationToken token = default)
    {
        var tops = localPaths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToList();
        if (tops.Any(t => NameProblem(t) is not null)) throw new ServerConnectionException("A file name cannot be uploaded (control characters or too long).");
        string target = Absolute(relative);
        string script = Guard + $$"""
            set -e
            d=$(inside {{Q(target)}})
            [ -d "$d" ] || { echo "ERROR: the target is not a folder"; exit 3; }
            tar -x -f - -C "$d" --no-same-owner --no-same-permissions {{(skipExisting ? "--skip-old-files" : "--overwrite")}}
            {{string.Join('\n', tops.Select(t => $"p=$(inside {Q(target + "/" + t)}); [ ! -e \"$p\" ] || fix_rights \"$p\""))}}
            echo OK
            """;
        CommandResult result = await connection.RunAsRootWithInputAsync(script, async (stdin, ct) =>
        {
            long sent = 0;
            await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
            foreach (string path in localPaths)
            {
                string top = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (Directory.Exists(path))
                {
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, top + "/"), ct);
                    foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = top + "/" + Path.GetRelativePath(path, entry).Replace('\\', '/');
                        if (name.Any(char.IsControl)) continue;
                        if (Directory.Exists(entry)) await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/"), ct);
                        else
                        {
                            await tar.WriteEntryAsync(entry, name, ct);
                            sent += new FileInfo(entry).Length;
                            progress(sent);
                        }
                    }
                }
                else
                {
                    await tar.WriteEntryAsync(path, top, ct);
                    sent += new FileInfo(path).Length;
                    progress(sent);
                }
            }
        }, token);
        if (!result.Succeeded || !result.Output.Contains("OK", StringComparison.Ordinal))
        {
            throw new ServerConnectionException("Uploading failed: " + ErrorOf(result));
        }
    }
}
