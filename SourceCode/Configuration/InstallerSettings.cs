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
    /// <summary>The branch chosen per repository ("duckietm/Octane" to "dev"); a repository that is not in here uses main.</summary>
    public Dictionary<string, string> Branches { get; set; } = [];

    public const string DefaultBranch = "main";

    /// <summary>A branch to choose from, with the line the lists show behind its name.</summary>
    public sealed record BranchChoice(string Branch, string Caption)
    {
        public override string ToString() => $"{Branch}   ({Caption})";
    }

    /// <summary>
    /// What the Settings page and the clone steps offer. One repository writes it as "dev" and another as
    /// "Dev"; the clone step matches the name case-insensitively against the branches the remote has.
    /// </summary>
    public static readonly BranchChoice[] BranchChoices =
    [
        new(DefaultBranch, "stable, the version the hotel runs on"),
        new("dev", "the newest changes, not always stable")
    ];

    /// <summary>The entry for a branch name, or main.</summary>
    public static BranchChoice ChoiceFor(string branch) =>
        BranchChoices.FirstOrDefault(b => b.Branch == branch) ?? BranchChoices[0];

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

    /// <summary>The branch chosen for a repository written as "owner/name", or main.</summary>
    public string BranchFor(string repository) =>
        Branches.TryGetValue(repository, out string? branch) && branch is { Length: > 0 } ? branch : DefaultBranch;

    /// <summary>Remembers the branch; main is the default, so it is not written.</summary>
    public void SetBranch(string repository, string branch)
    {
        if (branch == DefaultBranch) Branches.Remove(repository);
        else Branches[repository] = branch;
    }
}
