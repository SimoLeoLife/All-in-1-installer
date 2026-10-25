using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllInOneInstaller.Configuration;

/// <summary>
/// Defaults from the Settings page that every wizard fills in. Stored in configuration/settings.json so the
/// settings backup includes them.
/// </summary>
public sealed class InstallerSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>"host" or "host:port", as typed in the wizards.</summary>
    public string? DefaultServer { get; set; }
    /// <summary>Private key path relative to the configuration folder.</summary>
    public string? DefaultKey { get; set; }
    /// <summary>The All-in-1 downloader's executable. Deploy Assets reads Habbo_Default from its folder.</summary>
    public string? DownloaderPath { get; set; }

    private static string File => Path.Combine(ClientConfiguration.Directory, "settings.json");

    public static InstallerSettings Load()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<InstallerSettings>(System.IO.File.ReadAllText(File), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save() => System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json));

    /// <summary>The default key as a full path, when it still exists.</summary>
    [JsonIgnore]
    public string? DefaultKeyPath =>
        DefaultKey is { Length: > 0 } key && Path.Combine(ClientConfiguration.Directory, key) is var path && System.IO.File.Exists(path) ? path : null;

    public static string RelativeKey(string fullPath) => Path.GetRelativePath(ClientConfiguration.Directory, fullPath).Replace('\\', '/');
}
