using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AllInOneInstaller.Configuration;

/// <summary>
/// The configuration directory (SSH keys, .ppk files, MariaDB passwords of every server) as one file encrypted
/// with a password, to move it to another computer. AES-256-GCM; the key comes from the password with
/// PBKDF2-SHA256.
/// </summary>
public static class SettingsBackup
{
    public const string Extension = ".aio-settings";
    public const int MinimumPasswordLength = 8;

    private static readonly byte[] Magic = "AIOSET01"u8.ToArray();
    private const int Iterations = 600_000;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;

    /// <summary>Returns how many files went into <paramref name="file"/>.</summary>
    public static int Export(string file, string password)
    {
        byte[] zip = Zip(ClientConfiguration.Directory, out int count);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] cipher = new byte[zip.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, zip, cipher, tag, Magic);

        using FileStream output = File.Create(file);
        output.Write(Magic);
        output.Write(salt);
        output.Write(BitConverter.GetBytes(Iterations));
        output.Write(nonce);
        output.Write(tag);
        output.Write(cipher);
        return count;
    }

    /// <summary>
    /// Replaces the configuration directory with the backup; the current one is kept next to it as
    /// configuration.bak-&lt;time&gt;. Returns that folder (null when there was nothing) and the file count.
    /// </summary>
    public static (string? Previous, int Files) Import(string file, string password)
    {
        byte[] data = File.ReadAllBytes(file);
        int header = Magic.Length + SaltSize + sizeof(int) + NonceSize + TagSize;
        if (data.Length < header || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("This is not a settings backup of the All-in-1 Installer.");
        int at = Magic.Length;
        byte[] salt = data[at..(at += SaltSize)];
        int iterations = BitConverter.ToInt32(data, at);
        at += sizeof(int);
        byte[] nonce = data[at..(at += NonceSize)];
        byte[] tag = data[at..(at += TagSize)];
        byte[] cipher = data[at..];
        if (iterations is < 100_000 or > 10_000_000) throw new InvalidDataException("The settings backup is damaged.");

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] zip = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, zip, Magic);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("Wrong password, or the file is damaged.");
        }

        string target = ClientConfiguration.Directory;
        string parent = Path.GetDirectoryName(target)!;
        string incoming = Path.Combine(parent, $".configuration-import-{Guid.NewGuid():N}");
        int count = Unzip(zip, incoming);

        string? previous = null;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            previous = Path.Combine(parent, $"configuration.bak-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.Move(target, previous);
        }
        else if (Directory.Exists(target))
        {
            Directory.Delete(target);
        }
        Directory.Move(incoming, target);
        return (previous, count);
    }

    private static byte[] Zip(string directory, out int count)
    {
        count = 0;
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (name.StartsWith('.')) continue; // the write probe
                archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                count++;
            }
        }
        return buffer.ToArray();
    }

    // Owner-only permissions on every folder and file, like the installer writes them.
    private static int Unzip(byte[] zip, string directory)
    {
        Directory.CreateDirectory(directory);
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        int count = 0;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                string path = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Unsafe path in the backup: {entry.FullName}");
                string folder = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(folder);
                entry.ExtractToFile(path, overwrite: true);
                count++;
            }
            ClientConfiguration.RestrictToOwner(directory, directory: true);
            foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                ClientConfiguration.RestrictToOwner(folder, directory: true);
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                ClientConfiguration.RestrictToOwner(path, directory: false);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
        return count;
    }
}
