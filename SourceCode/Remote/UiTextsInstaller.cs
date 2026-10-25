using System.Text.RegularExpressions;

namespace AllInOneInstaller.Remote;

/// <param name="Languages">Codes that have a UITexts_&lt;code&gt;.jsonc.example, from the Octane clone or the known list.</param>
/// <param name="Current">The language UITexts.jsonc is a copy of, "custom" if it matches none, or null if there is no file.</param>
/// <param name="FromClone">Whether the examples come from the Octane clone on the server rather than GitHub.</param>
public sealed record UiTextsState(IReadOnlyList<string> Languages, string? Current, bool FromClone)
{
    public bool Exists => Current is not null;
}

/// <summary>
/// Installs one of Octane's per-language UITexts_&lt;code&gt;.jsonc.example files as ${gamedata.url}/config/UITexts.jsonc,
/// which the renderer loads for the client's interface texts.
/// </summary>
public static partial class UiTextsInstaller
{
    public const string Target = OctaneInstaller.Gamedata + "/config/UITexts.jsonc";
    private const string RawUrl = "https://raw.githubusercontent.com/duckietm/Octane/main/public/configuration";

    /// <summary>Octane's languages, used when there is no clone to look in.</summary>
    public static readonly string[] KnownLanguages = ["ar", "de", "en", "es", "fr", "it", "nl"];

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "Arabic", ["de"] = "German (Deutsch)", ["en"] = "English", ["es"] = "Spanish (Español)",
        ["fr"] = "French (Français)", ["it"] = "Italian (Italiano)", ["nl"] = "Dutch (Nederlands)", ["pt"] = "Portuguese (Português)",
        ["br"] = "Brazilian Portuguese (Português)", ["tr"] = "Turkish (Türkçe)", ["fi"] = "Finnish (Suomi)", ["pl"] = "Polish (Polski)"
    };

    public static string Name(string code) => Names.TryGetValue(code, out string? name) ? $"{name} · {code}" : code;

    [GeneratedRegex("^[A-Za-z]{2,3}([_-][A-Za-z]{2})?$")]
    private static partial Regex LanguageCode();

    private static string Example(string code) => $"UITexts_{code}.jsonc.example";

    public static async Task<UiTextsState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            dir={{OctaneInstaller.ConfigurationDirectory}}
            target={{Target}}
            langs=""
            for f in "$dir"/UITexts_*.jsonc.example; do
              [ -f "$f" ] || continue
              l=${f##*/UITexts_}; langs="$langs ${l%.jsonc.example}"
            done
            echo "LANGS=$langs"
            if [ -f "$target" ]; then
              current=custom
              for l in $langs; do cmp -s "$target" "$dir/UITexts_$l.jsonc.example" && { current=$l; break; }; done
              echo "CURRENT=$current"
            fi
            """;
        var values = new ScriptValues((await connection.RunAsRootAsync(script, token)).Output);
        string[] found = (values["LANGS"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => LanguageCode().IsMatch(l)).ToArray();
        string? current = values["CURRENT"];
        // Without a clone the file is not compared with GitHub's copies, so it reads as "custom" and is backed up when replaced.
        return new UiTextsState(found.Length > 0 ? found : KnownLanguages, current, found.Length > 0);
    }

    /// <summary>Returns the path of the backup of the previous file, if one was made.</summary>
    public static async Task<string?> InstallAsync(ServerConnection connection, string code, Action<string> log, CancellationToken token = default)
    {
        if (!LanguageCode().IsMatch(code)) throw new ArgumentException($"Not a language code: {code}", nameof(code));
        string script = RemoteScript.Prelude + $$"""
            src={{OctaneInstaller.ConfigurationDirectory}}/{{Example(code)}}
            target={{Target}}
            tmp=$(mktemp)
            trap 'rm -f "$tmp"' EXIT
            if [ -f "$src" ]; then
              echo "> $src"
              cp "$src" "$tmp"
            else
              echo "> Not in the Octane clone: downloading {{RawUrl}}/{{Example(code)}}"
              download "{{RawUrl}}/{{Example(code)}}" "$tmp" || fail "Downloading {{Example(code)}} failed"
            fi
            # JSON with comments, so the first character that is not a space or comment must be '{'.
            [ -s "$tmp" ] && sed -e 's#//.*##' "$tmp" | tr -d ' \t\r\n\357\273\277' | grep -q '^{' || fail "{{Example(code)}} is not a UITexts file"
            echo "  $(grep -c '":' "$tmp") texts"
            install -d -m 2775 -o www-data -g www-data "${target%/*}"
            # Back up only a custom file, not one of Octane's languages.
            own=no
            if [ -f "$target" ] && ! cmp -s "$target" "$tmp"; then
              own=yes
              for f in {{OctaneInstaller.ConfigurationDirectory}}/UITexts_*.jsonc.example; do
                [ -f "$f" ] && cmp -s "$target" "$f" && own=no
              done
            fi
            if [ "$own" = yes ]; then
              backup="$target.bak-$(date +%Y%m%d%H%M%S)"
              cp -p "$target" "$backup"
              echo "  The previous UITexts.jsonc is kept as $backup"
              echo "BACKUP=$backup"
            fi
            install -m 664 -o www-data -g www-data "$tmp" "$target"
            echo "> $target ({{code}})"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing UITexts.jsonc failed", ["BACKUP"], token);
        return result["BACKUP"];
    }
}
