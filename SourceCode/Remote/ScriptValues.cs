namespace AllInOneInstaller.Remote;

/// <summary>"key=value" lines printed by a script. Empty values count as missing.</summary>
public sealed class ScriptValues
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ScriptValues(string output) : this(output.Split('\n')) { }

    public ScriptValues(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            int equals = line.IndexOf('=');
            if (equals > 0) _values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
    }

    public string? this[string key] => _values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    public bool Yes(string key) => this[key] == "yes";

    public int Int(string key) => int.TryParse(this[key], out int value) ? value : 0;
}
