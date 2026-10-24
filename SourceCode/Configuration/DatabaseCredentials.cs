using System.Security.Cryptography;

namespace AllInOneInstaller.Configuration;

/// <summary>MariaDB accounts for one server. The emulator account is filled in by the emulator step.</summary>
public sealed record DatabaseCredentials(string Host, int Port, string Database, string RootUser, string RootPassword,
    string CmsUser, string CmsPassword, string EmulatorUser = "emulator", string EmulatorPassword = "")
{
    // Alphanumeric so passwords are safe in any config file or connection string.
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    public const int PasswordLength = 32; // ~190 bits

    public static string NewPassword() => RandomNumberGenerator.GetString(Alphabet, PasswordLength);

    public static DatabaseCredentials Generate(string host) =>
        new(host, 3306, "habbo", "root", NewPassword(), "cms", NewPassword());

    public string ToFileText() => $"""
        # MariaDB accounts on {Host}, created by All-in-1 Installer.
        # Keep this file safe: root can change every database on the server.
        host={Host}
        port={Port}
        database={Database}
        root_user={RootUser}
        root_password={RootPassword}
        cms_user={CmsUser}
        cms_password={CmsPassword}
        emulator_user={EmulatorUser}
        emulator_password={EmulatorPassword}

        """;

    public static DatabaseCredentials? Parse(string text)
    {
        var values = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        string? Get(string key) => values.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;
        if (Get("host") is not { } host || Get("root_password") is not { } rootPassword || Get("cms_password") is not { } cmsPassword)
            return null;
        return new DatabaseCredentials(host, int.TryParse(Get("port"), out int port) ? port : 3306, Get("database") ?? "habbo",
            Get("root_user") ?? "root", rootPassword, Get("cms_user") ?? "cms", cmsPassword,
            Get("emulator_user") ?? "emulator", Get("emulator_password") ?? "");
    }
}
