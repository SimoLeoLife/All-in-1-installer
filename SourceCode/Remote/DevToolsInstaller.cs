namespace AllInOneInstaller.Remote;

/// <summary>Node.js, Composer and Yarn as found on the server before the step runs.</summary>
public sealed record DevToolsState(
    string? NodeVersion,
    bool NodeFromNodeSource,
    string? YarnVersion,
    string? ComposerVersion,
    bool PhpInstalled,
    IReadOnlyList<string> Shadowing)
{
    public int? NodeMajor => NodeVersion is not null && int.TryParse(NodeVersion.TrimStart('v').Split('.')[0], out int major) ? major : null;

    public bool IsComplete => NodeFromNodeSource && NodeMajor == DevToolsInstaller.NodeMajor && YarnVersion is not null && ComposerVersion is not null;
}

/// <summary>Installed versions, plus older copies that come first in root's PATH.</summary>
public sealed record DevToolsVersions(string Node, string Npm, string Yarn, string Composer, IReadOnlyList<string> Shadowing);

/// <summary>Installs Node.js from NodeSource, Composer from getcomposer.org and Yarn through Corepack.</summary>
public static class DevToolsInstaller
{
    public const int NodeMajor = 26;

    // "NSolid <nsolid-gpg@nodesource.com>", which signs deb.nodesource.com.
    public const string NodeSourceKeyFingerprint = "6F71F525282841EEDAF851B42F59B5F99B1BE0B4";

    // Pins PATH to the /usr/bin tools this step installs. shadowing() lists older copies earlier in root's PATH.
    private const string ToolEnvironment = """
        login_path="$PATH"
        export PATH=/usr/sbin:/usr/bin:/sbin:/bin
        shadowing() {
          for t in node npm yarn composer; do
            p=$(PATH="$login_path" command -v "$t" 2>/dev/null) || continue
            [ "$p" != "/usr/bin/$t" ] && printf '%s ' "$p"
          done
          return 0
        }
        # Composer refuses to run as root without this, and the installer runs as root on purpose.
        export COMPOSER_ALLOW_SUPERUSER=1
        # Keep npm's update notices out of the log.
        export npm_config_update_notifier=false
        # Corepack would otherwise ask before downloading Yarn.
        export COREPACK_ENABLE_DOWNLOAD_PROMPT=0
        """ + "\n";

    public static async Task<DevToolsState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = ToolEnvironment + """
            echo "node=$(node --version 2>/dev/null)"
            echo "nodesource=$(dpkg-query -W -f='${Version}' nodejs 2>/dev/null | grep -q nodesource && echo yes)"
            echo "yarn=$(cd /tmp && yarn --version 2>/dev/null)"
            echo "composer=$(composer --version --no-ansi 2>/dev/null | awk '{print $3}')"
            echo "php=$(command -v php >/dev/null 2>&1 && echo yes)"
            echo "shadowing=$(shadowing)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);
        return new DevToolsState(values["node"], values.Yes("nodesource"), values["yarn"], values["composer"], values.Yes("php"),
            (values["shadowing"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static Task AddNodeSourceAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get update"
            apt_update
            echo "> Installing prerequisites"
            apt-get install -y --no-install-recommends ca-certificates curl gnupg
            install -d -m 0755 /etc/apt/keyrings

            echo "> Downloading and checking the NodeSource signing key"
            tmp=$(mktemp)
            trap 'rm -f "$tmp"' EXIT
            curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key -o "$tmp" || fail "Could not download the NodeSource signing key."
            fprs=$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '/^pub/{p=1; next} /^fpr/ && p {print $10; p=0}')
            [ "$fprs" = "{{NodeSourceKeyFingerprint}}" ] || fail "The downloaded key is not the NodeSource key {{NodeSourceKeyFingerprint}} (found: $fprs)."
            echo "  key {{NodeSourceKeyFingerprint}} ok"
            gpg --dearmor --yes -o /etc/apt/keyrings/nodesource.gpg "$tmp"

            echo "> Adding https://deb.nodesource.com/node_{{NodeMajor}}.x"
            echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_{{NodeMajor}}.x nodistro main" > /etc/apt/sources.list.d/nodesource.list
            # Always take Node.js from NodeSource, even where the distribution ships its own nodejs package.
            printf 'Package: nodejs\nPin: origin deb.nodesource.com\nPin-Priority: 900\n' > /etc/apt/preferences.d/99nodesource
            apt_update
            """;
        return RemoteScript.RunAsync(connection, script, log, "Adding the NodeSource repository failed", token);
    }

    /// <summary>Returns the installed version, such as "v26.12.1". npm comes with the nodejs package.</summary>
    public static async Task<string> InstallNodeAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + ToolEnvironment + $$"""
            echo "> apt-get install nodejs"
            apt-get install -y nodejs
            version=$(node --version)
            case "$version" in v{{NodeMajor}}.*) ;; *) fail "node $version is installed instead of Node.js {{NodeMajor}}." ;; esac
            echo "  node $version, npm $(npm --version)"
            echo "VERSION=$version"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing Node.js failed", ["VERSION"], token);
        return result["VERSION"] ?? "";
    }

    // Installs the latest stable composer.phar after checking its published SHA-256.
    // composer-setup.php is avoided because it downloads the phar without a timeout.
    public static Task InstallComposerAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + ToolEnvironment + """
            command -v php >/dev/null 2>&1 || fail "Composer needs PHP, which is not installed. Run the PHP step first."
            tmp=$(mktemp -d)
            trap 'rm -rf "$tmp"' EXIT
            base=https://getcomposer.org/download/latest-stable
            echo "> Downloading $base/composer.phar"
            download "$base/composer.phar.sha256" "$tmp/composer.phar.sha256" || fail "Could not download the Composer checksum from getcomposer.org."
            download "$base/composer.phar" "$tmp/composer.phar" || fail "Could not download Composer from getcomposer.org."
            expected=$(tr -dc '0-9a-f' < "$tmp/composer.phar.sha256")
            echo "$expected  $tmp/composer.phar" | sha256sum -c --status || fail "The downloaded Composer does not match its published checksum."
            echo "  checksum ok (sha256 $expected)"
            php "$tmp/composer.phar" --version --no-ansi >/dev/null 2>&1 || fail "The downloaded composer.phar does not run."
            install -m 755 "$tmp/composer.phar" /usr/bin/composer
            echo "  installed as /usr/bin/composer"
            composer --version --no-ansi 2>/dev/null
            """;
        return RemoteScript.RunAsync(connection, script, log, "Installing Composer failed", token);
    }

    public static Task InstallYarnAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + ToolEnvironment + """
            echo "> npm install -g corepack"
            npm install -g --no-fund --no-audit corepack
            echo "> corepack enable"
            corepack enable
            # Projects pin Yarn with packageManager in package.json. This sets the default everywhere else.
            echo "> corepack install --global yarn@stable"
            corepack install --global yarn@stable
            """;
        return RemoteScript.RunAsync(connection, script, log, "Installing Yarn failed", token);
    }

    public static async Task<DevToolsVersions> VersionsAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + ToolEnvironment + """
            cd /tmp
            echo "NODE=$(node --version)"
            echo "NPM=$(npm --version)"
            echo "YARN=$(yarn --version)"
            echo "COMPOSER=$(composer --version --no-ansi 2>/dev/null | awk 'NR==1 {print $3}')"
            echo "SHADOWING=$(shadowing)"
            """;
        var lines = new List<string>();
        await RemoteScript.RunAsync(connection, script, line => { lines.Add(line); log(line); }, "Checking the versions failed", token);
        var values = new ScriptValues(lines);
        string Get(string key) => values[key] ?? throw new ServerConnectionException($"{key.ToLowerInvariant()} --version printed nothing.");
        return new DevToolsVersions(Get("NODE"), Get("NPM"), Get("YARN"), Get("COMPOSER"),
            (values["SHADOWING"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
