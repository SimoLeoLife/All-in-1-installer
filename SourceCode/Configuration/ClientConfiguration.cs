using AllInOneInstaller.Remote;

namespace AllInOneInstaller.Configuration;

/// <summary>
/// The local "configuration" directory, next to the installer or, if that is read-only, under LocalApplicationData.
/// </summary>
public static class ClientConfiguration
{
    private static string? _directory;

    public static string Directory => _directory ??= Resolve();

    public static string KeysDirectory(string host)
    {
        string path = Path.Combine(Directory, "keys", SafeName(host));
        System.IO.Directory.CreateDirectory(path);
        RestrictToOwner(path, directory: true);
        return path;
    }

    /// <summary>Returns the private key path; the public and PuTTY keys get ".pub" and ".ppk" appended.</summary>
    public static string SaveKeyPair(string host, SshKeyPair pair)
    {
        string path = FreePath(KeysDirectory(host), pair.FileName, ".pub");
        // OpenSSH refuses private keys that others can read.
        File.WriteAllText(path, pair.PrivateKeyPem);
        RestrictToOwner(path, directory: false);
        File.WriteAllText(path + ".pub", pair.PublicKey.Line + "\n");
        File.WriteAllText(path + ".ppk", pair.PuttyKey);
        RestrictToOwner(path + ".ppk", directory: false);
        return path;
    }

    public static string SavePublicKey(string host, AuthorizedKey key)
    {
        string name = "provided_" + key.Type.Split('@')[0].Replace("ssh-", "");
        string path = FreePath(KeysDirectory(host), name, "") + ".pub";
        File.WriteAllText(path, key.Line + "\n");
        return path;
    }

    // Newest first.
    public static List<(string Path, AuthorizedKey Key)> SavedPublicKeys(string host)
    {
        var keys = new List<(string, AuthorizedKey)>();
        foreach (string path in System.IO.Directory.GetFiles(KeysDirectory(host), "*.pub").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                if (SshKeys.TryParsePublicKey(File.ReadAllText(path), out AuthorizedKey key, out _)) keys.Add((path, key));
            }
            catch (IOException) { }
        }
        return keys;
    }

    /// <summary>Private keys saved by the SSH key step (a key file with its .pub next to it), newest first.</summary>
    public static List<string> SavedPrivateKeys(string host)
    {
        string directory = Path.Combine(Directory, "keys", SafeName(host));
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.GetFiles(directory)
            .Where(path => Path.GetExtension(path) is not (".pub" or ".ppk") && File.Exists(path + ".pub"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public static string DatabaseCredentialsPath(string host)
    {
        string directory = Path.Combine(Directory, "database", SafeName(host));
        System.IO.Directory.CreateDirectory(directory);
        RestrictToOwner(directory, directory: true);
        return Path.Combine(directory, "mariadb.txt");
    }

    public static DatabaseCredentials? LoadDatabaseCredentials(string host)
    {
        string path = DatabaseCredentialsPath(host);
        return File.Exists(path) ? DatabaseCredentials.Parse(File.ReadAllText(path)) : null;
    }

    // A changed file is backed up as mariadb.txt.bak-<time>.
    public static string SaveDatabaseCredentials(DatabaseCredentials credentials)
    {
        string path = DatabaseCredentialsPath(credentials.Host);
        if (File.Exists(path) && File.ReadAllText(path) != credentials.ToFileText())
            File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        // Restrict the empty file before writing so the passwords are never readable by others.
        File.WriteAllText(path, "");
        RestrictToOwner(path, directory: false);
        File.WriteAllText(path, credentials.ToFileText());
        return path;
    }

    // Appends a timestamp when the name is taken so no key is ever overwritten.
    private static string FreePath(string directory, string name, string companionSuffix)
    {
        string path = Path.Combine(directory, name);
        bool Taken(string p) => File.Exists(p) || File.Exists(p + ".pub") || File.Exists(p + ".ppk") ||
                                (companionSuffix.Length > 0 && File.Exists(p + companionSuffix));
        if (!Taken(path)) return path;
        string stamped = Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}");
        for (int i = 2; Taken(stamped); i++) stamped = Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}_{i}");
        return stamped;
    }

    private static string SafeName(string host) =>
        new(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    private static string Resolve()
    {
        string besideApp = Path.Combine(AppContext.BaseDirectory, "configuration");
        if (IsWritable(besideApp)) return besideApp;
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "All-in-1 Installer", "configuration");
        System.IO.Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            RestrictToOwner(directory, directory: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestrictToOwner(string path, bool directory)
    {
        if (OperatingSystem.IsWindows()) return; // relies on inherited folder permissions
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
