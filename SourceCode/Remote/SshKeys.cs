using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AllInOneInstaller.Remote;

public enum SshKeyType { Ed25519, Rsa4096 }

/// <summary>A public key as "type base64 comment", the format of authorized_keys and .pub files.</summary>
public sealed record AuthorizedKey(string Type, string Base64, string Comment)
{
    public string Line => Comment.Length > 0 ? $"{Type} {Base64} {Comment}" : $"{Type} {Base64}";

    // As printed by ssh-keygen -l.
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Base64))).TrimEnd('=');

    // Ignores options and comments.
    public bool SameKey(AuthorizedKey other) => Type == other.Type && Base64 == other.Base64;
}

/// <summary>A generated key pair, with the private key in both OpenSSH and PuTTY .ppk format.</summary>
public sealed record SshKeyPair(SshKeyType KeyType, string PrivateKeyPem, string PuttyKey, AuthorizedKey PublicKey)
{
    public string FileName => KeyType == SshKeyType.Ed25519 ? "id_ed25519" : "id_rsa";
}

public static class SshKeys
{
    private static readonly string[] SupportedTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com"
    ];

    // ssh-keygen's default.
    public const int KdfRounds = 16;

    /// <summary>An empty <paramref name="passphrase"/> leaves the private key unencrypted.</summary>
    public static SshKeyPair Generate(SshKeyType type, string comment, string passphrase)
    {
        comment = CleanComment(comment);
        return type == SshKeyType.Ed25519 ? GenerateEd25519(comment, passphrase) : GenerateRsa(comment, passphrase);
    }

    private static SshKeyPair GenerateEd25519(string comment, string passphrase)
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        byte[] seed = privateKey.GetEncoded();
        byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();

        byte[] publicBlob = Blob(w => { w.String("ssh-ed25519"); w.Bytes(publicKey); });
        byte[] privateFields = Blob(w =>
        {
            w.String("ssh-ed25519");
            w.Bytes(publicKey);
            w.Bytes([.. seed, .. publicKey]); // OpenSSH stores seed || public key
        });
        // PuTTY stores only the seed.
        string putty = PuttyPrivateKey("ssh-ed25519", publicBlob, Blob(w => w.Bytes(seed)), comment, passphrase);
        return new SshKeyPair(SshKeyType.Ed25519, OpenSshPrivateKey(publicBlob, privateFields, comment, passphrase), putty,
            new AuthorizedKey("ssh-ed25519", Convert.ToBase64String(publicBlob), comment));
    }

    private static SshKeyPair GenerateRsa(string comment, string passphrase)
    {
        using RSA rsa = RSA.Create(4096);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);

        byte[] publicBlob = Blob(w => { w.String("ssh-rsa"); w.MpInt(p.Exponent!); w.MpInt(p.Modulus!); });
        byte[] privateFields = Blob(w =>
        {
            w.String("ssh-rsa");
            w.MpInt(p.Modulus!);
            w.MpInt(p.Exponent!);
            w.MpInt(p.D!);
            w.MpInt(p.InverseQ!);
            w.MpInt(p.P!);
            w.MpInt(p.Q!);
        });
        // PuTTY stores d, p, q and q^-1 mod p. n and e are in the public part.
        byte[] puttyPrivate = Blob(w => { w.MpInt(p.D!); w.MpInt(p.P!); w.MpInt(p.Q!); w.MpInt(p.InverseQ!); });
        string putty = PuttyPrivateKey("ssh-rsa", publicBlob, puttyPrivate, comment, passphrase);
        return new SshKeyPair(SshKeyType.Rsa4096, OpenSshPrivateKey(publicBlob, privateFields, comment, passphrase), putty,
            new AuthorizedKey("ssh-rsa", Convert.ToBase64String(publicBlob), comment));
    }

    // "openssh-key-v1" as ssh-keygen writes it, with aes256-ctr and a bcrypt_pbkdf key when there is a passphrase.
    private static string OpenSshPrivateKey(byte[] publicBlob, byte[] privateFields, string comment, string passphrase)
    {
        bool encrypted = passphrase.Length > 0;
        int blockSize = encrypted ? 16 : 8;
        uint check = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        byte[] privateSection = Blob(w =>
        {
            w.UInt32(check);
            w.UInt32(check);
            w.Raw(privateFields);
            w.String(comment);
            // Padded with 1, 2, 3, ... to the cipher block size.
            for (byte pad = 1; w.Length % blockSize != 0; pad++) w.Raw([pad]);
        });

        byte[] kdfOptions = [];
        if (encrypted)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            kdfOptions = Blob(w => { w.Bytes(salt); w.UInt32(KdfRounds); });
            byte[] keyAndIv = BcryptPbkdf.DeriveKey(Encoding.UTF8.GetBytes(passphrase), salt, 32 + 16, KdfRounds);
            privateSection = Aes256Ctr(keyAndIv[..32], keyAndIv[32..], privateSection);
            CryptographicOperations.ZeroMemory(keyAndIv);
        }
        byte[] body = Blob(w =>
        {
            w.Raw("openssh-key-v1\0"u8.ToArray());
            w.String(encrypted ? "aes256-ctr" : "none");
            w.String(encrypted ? "bcrypt" : "none");
            w.Bytes(kdfOptions);
            w.UInt32(1);
            w.Bytes(publicBlob);
            w.Bytes(privateSection);
        });

        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        string base64 = Convert.ToBase64String(body);
        for (int i = 0; i < base64.Length; i += 70) pem.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return pem.ToString();
    }

    // PPK v2, which every PuTTY since 0.52 reads, HeidiSQL's plink included. v3 would need Argon2.
    // Encryption is AES-256-CBC with a zero IV and the key SHA1(0000 || pass) || SHA1(0001 || pass) cut to 32 bytes.
    // The MAC is HMAC-SHA1 keyed with SHA1("putty-private-key-file-mac-key" || pass) over the algorithm, encryption,
    // comment, public blob and padded private plaintext.
    private static string PuttyPrivateKey(string algorithm, byte[] publicBlob, byte[] privateBlob, string comment, string passphrase)
    {
        bool encrypted = passphrase.Length > 0;
        string encryption = encrypted ? "aes256-cbc" : "none";
        byte[] secret = Encoding.UTF8.GetBytes(passphrase);

        // Padded to the AES block size. Readers ignore what follows the key, and the MAC covers it.
        byte[] plain = privateBlob;
        if (encrypted && plain.Length % 16 != 0)
            plain = [.. plain, .. RandomNumberGenerator.GetBytes(16 - plain.Length % 16)];

        byte[] macKey = SHA1.HashData([.. "putty-private-key-file-mac-key"u8, .. secret]);
        byte[] macData = Blob(w =>
        {
            w.String(algorithm);
            w.String(encryption);
            w.String(comment);
            w.Bytes(publicBlob);
            w.Bytes(plain);
        });
        string mac = Convert.ToHexStringLower(HMACSHA1.HashData(macKey, macData));

        byte[] stored = plain;
        if (encrypted)
        {
            byte[] key = [.. SHA1.HashData([0, 0, 0, 0, .. secret]), .. SHA1.HashData([0, 0, 0, 1, .. secret])];
            using Aes aes = Aes.Create();
            aes.Key = key[..32];
            stored = aes.EncryptCbc(plain, new byte[16], PaddingMode.None);
            CryptographicOperations.ZeroMemory(key);
        }
        CryptographicOperations.ZeroMemory(macKey);

        static void Lines(StringBuilder text, string label, byte[] data)
        {
            string base64 = Convert.ToBase64String(data);
            int count = (base64.Length + 63) / 64;
            text.Append(label).Append(": ").Append(count).Append('\n');
            for (int i = 0; i < base64.Length; i += 64) text.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        var ppk = new StringBuilder();
        ppk.Append("PuTTY-User-Key-File-2: ").Append(algorithm).Append('\n');
        ppk.Append("Encryption: ").Append(encryption).Append('\n');
        ppk.Append("Comment: ").Append(comment).Append('\n');
        Lines(ppk, "Public-Lines", publicBlob);
        Lines(ppk, "Private-Lines", stored);
        ppk.Append("Private-MAC: ").Append(mac).Append('\n');
        return ppk.ToString();
    }

    // The IV is a 128-bit big-endian counter.
    private static byte[] Aes256Ctr(byte[] key, byte[] iv, byte[] data)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        byte[] counter = (byte[])iv.Clone();
        byte[] counters = new byte[data.Length];
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            counter.CopyTo(counters, offset);
            for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        byte[] stream = aes.EncryptEcb(counters, PaddingMode.None);
        for (int i = 0; i < data.Length; i++) stream[i] ^= data[i];
        return stream;
    }

    public static bool IsPrivateKey(string text) =>
        text.Contains("PRIVATE KEY", StringComparison.Ordinal) || text.TrimStart().StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal);

    /// <summary>Thrown by <see cref="PublicKeyOf"/> when the private key is encrypted and the password is missing or wrong.</summary>
    public sealed class KeyPasswordException(string message) : Exception(message);

    /// <summary>Reads the public key from an OpenSSH, PEM or .ppk private key the same way SSH.NET does when logging in.</summary>
    public static AuthorizedKey PublicKeyOf(string privateKeyText, string passphrase, string comment)
    {
        Renci.SshNet.PrivateKeyFile file;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyText.Trim() + "\n"));
            file = new Renci.SshNet.PrivateKeyFile(stream, passphrase.Length > 0 ? passphrase : null);
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            throw new KeyPasswordException("This key has a password: enter it.");
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
        {
            if (passphrase.Length > 0) throw new KeyPasswordException("The key could not be opened: wrong key password?");
            throw new FormatException($"This is not a private key the installer can use: {ex.Message}");
        }
        using (file)
        {
            // The public key blob starts with its type string.
            byte[] blob = file.HostKeyAlgorithms.First().Data;
            int length = BinaryPrimitives.ReadInt32BigEndian(blob);
            string type = Encoding.ASCII.GetString(blob, 4, length);
            return new AuthorizedKey(type, Convert.ToBase64String(blob), CleanComment(comment));
        }
    }

    /// <summary>Accepts the OpenSSH one-line format or the RFC 4716 format PuTTYgen saves.</summary>
    public static bool TryParsePublicKey(string text, out AuthorizedKey key, out string error)
    {
        key = null!;
        text = text.Trim();
        if (text.Length == 0) { error = "Paste a public key or load a .pub file."; return false; }
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal) || text.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            error = "This is a private key. Never share it: use the public key (the .pub file) instead.";
            return false;
        }

        AuthorizedKey? parsed = text.StartsWith("---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal)
            ? ParseRfc4716(text)
            : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray() is [string line]
                ? ParseLine(line)
                : null;
        if (parsed is null)
        {
            error = "That is not a valid public key. It should look like: ssh-ed25519 AAAAC3Nza... you@computer";
            return false;
        }
        if (!SupportedTypes.Contains(parsed.Type))
        {
            error = parsed.Type == "ssh-dss"
                ? "DSA keys are no longer accepted by OpenSSH. Use an Ed25519 or RSA key."
                : $"Key type '{parsed.Type}' is not supported.";
            return false;
        }
        key = parsed;
        error = "";
        return true;
    }

    /// <summary>Parses an authorized_keys line, skipping leading options. Returns null if it holds no valid key.</summary>
    public static AuthorizedKey? ParseLine(string line)
    {
        string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (!LooksLikeKeyType(parts[i])) continue;
            if (BlobType(parts[i + 1]) != parts[i]) continue;
            return new AuthorizedKey(parts[i], parts[i + 1], string.Join(' ', parts.Skip(i + 2)));
        }
        return null;
    }

    public static List<AuthorizedKey> ParseAuthorizedKeys(string content) =>
        content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(ParseLine)
            .OfType<AuthorizedKey>()
            .ToList();

    private static AuthorizedKey? ParseRfc4716(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        int end = lines.FindIndex(l => l.StartsWith("---- END SSH2 PUBLIC KEY ----", StringComparison.Ordinal));
        if (end < 0) return null;
        string comment = "";
        var base64 = new StringBuilder();
        bool continued = false;
        foreach (string line in lines.Skip(1).Take(end - 1))
        {
            if (continued || line.Contains(':'))
            {
                // Header line. A trailing backslash continues it on the next line.
                if (!continued && line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                    comment = line["Comment:".Length..].Trim().Trim('"');
                continued = line.EndsWith('\\');
                continue;
            }
            base64.Append(line);
        }
        string blob = base64.ToString();
        string? type = BlobType(blob);
        return type is null ? null : new AuthorizedKey(type, blob, CleanComment(comment));
    }

    private static bool LooksLikeKeyType(string token) =>
        token.StartsWith("ssh-", StringComparison.Ordinal) || token.StartsWith("ecdsa-", StringComparison.Ordinal) ||
        token.StartsWith("sk-", StringComparison.Ordinal);

    // The type string at the start of the blob, or null if it is not a key blob.
    private static string? BlobType(string base64)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        if (blob.Length < 4) return null;
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length <= 0 || length > 64 || 4 + length > blob.Length) return null;
        return Encoding.ASCII.GetString(blob, 4, length);
    }

    // authorized_keys comments must be a single line.
    public static string CleanComment(string comment) =>
        string.Join(' ', comment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => new string(w.Where(c => !char.IsControl(c)).ToArray())));

    private static byte[] Blob(Action<SshWriter> write)
    {
        var writer = new SshWriter();
        write(writer);
        return writer.ToArray();
    }

    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();
        public long Length => _stream.Length;

        public void Raw(byte[] bytes) => _stream.Write(bytes);

        public void UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void Bytes(byte[] bytes)
        {
            UInt32((uint)bytes.Length);
            Raw(bytes);
        }

        public void String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        // Big-endian two's complement without leading zeros, plus a zero byte when the top bit is set.
        public void MpInt(byte[] unsignedBigEndian)
        {
            int start = 0;
            while (start < unsignedBigEndian.Length && unsignedBigEndian[start] == 0) start++;
            byte[] trimmed = unsignedBigEndian[start..];
            Bytes(trimmed.Length > 0 && (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
