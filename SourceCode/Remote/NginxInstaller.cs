namespace AllInOneInstaller.Remote;

/// <summary>NGINX as found on the server before the NGINX step runs.</summary>
public sealed record NginxState(
    string Codename,
    string? InstalledVersion,
    IReadOnlyList<string> DistroPackages,
    string? Port80Process,
    bool LayoutReady,
    bool Running)
{
    // Ubuntu/Debian packages conflict with nginx.org's package.
    public bool HasDistroNginx => DistroPackages.Count > 0;

    // nginx.org builds are versioned like "1.31.8-1~noble".
    public bool HasNginxOrgNginx => InstalledVersion is not null && !HasDistroNginx && InstalledVersion.Contains('~');

    // Traefik (and its docker-proxy) from the web access step is allowed on port 80.
    public bool Port80Blocked => Port80Process is not null && Port80Process is not ("nginx" or "traefik" or "docker-proxy");

    public bool IsComplete => HasNginxOrgNginx && LayoutReady;
}

/// <summary>
/// Installs NGINX mainline from nginx.org and recreates the Debian layout nginx-common would provide, since
/// nginx-common conflicts with nginx.org's package.
/// </summary>
public static class NginxInstaller
{
    // Published by nginx.org for verifying nginx_signing.key.
    public const string PrimaryKeyFingerprint = "573BFD6B3D8FBC641079A6ABABF5BD827BD9BF62";

    // Every key currently in nginx_signing.key; any other key is refused.
    private static readonly string[] KnownKeyFingerprints =
    [
        PrimaryKeyFingerprint,
        "8540A6F18833A80E9C1653A42FD21310B49F6B46",
        "9E9BE90EACBCDE69FE9B204CBCDCD8A38D88A2B3"
    ];


    public static async Task<NginxState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        const string script = """
            . /etc/os-release
            echo "codename=${VERSION_CODENAME:-$(lsb_release -cs 2>/dev/null)}"
            echo "version=$(dpkg-query -W -f='${db:Status-Abbrev}${Version}' nginx 2>/dev/null | awk '/^ii/{print substr($0,4)}')"
            echo "distro=$(dpkg-query -W -f='${db:Status-Abbrev}${Package}\n' nginx-common nginx-core nginx-full nginx-light nginx-extras 2>/dev/null | awk '/^ii/{printf "%s ", substr($0,4)}')"
            echo "port80=$(ss -Hltnp 'sport = :80' 2>/dev/null | grep -oE 'users:\(\("[^"]+"' | head -1 | cut -d'"' -f2)"
            echo "layout=$(grep -qs 'include /etc/nginx/sites-enabled/' /etc/nginx/nginx.conf && [ -d /etc/nginx/sites-available ] && echo yes)"
            echo "running=$(pgrep -x nginx >/dev/null && echo yes)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        if (!result.Succeeded) throw new ServerConnectionException("Could not inspect the server: " + result.Error.Trim());

        var values = new ScriptValues(result.Output);
        return new NginxState(
            values["codename"] ?? "",
            values["version"],
            (values["distro"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            values["port80"],
            values.Yes("layout"),
            values.Yes("running"));
    }

    // Pinned above the distribution.
    public static Task AddRepositoryAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string known = string.Join('|', KnownKeyFingerprints);
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get update"
            apt_update
            keyring=ubuntu-keyring; [ "$ID" = debian ] && keyring=debian-archive-keyring
            echo "> Installing prerequisites"
            apt-get install -y --no-install-recommends curl gnupg2 ca-certificates lsb-release "$keyring"

            url="https://nginx.org/packages/mainline/$ID"
            curl -fsSI "$url/dists/$codename/Release" >/dev/null || fail "nginx.org has no mainline packages for $PRETTY_NAME ($codename)."

            echo "> Downloading and checking the nginx.org signing key"
            tmp=$(mktemp)
            curl -fsSL https://nginx.org/keys/nginx_signing.key -o "$tmp" || fail "Could not download the nginx.org signing key."
            fprs=$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '/^pub/{p=1; next} /^fpr/ && p {print $10; p=0}')
            echo "$fprs" | grep -qx "{{PrimaryKeyFingerprint}}" || fail "The downloaded signing key is not the nginx.org key ({{PrimaryKeyFingerprint}})."
            for f in $fprs; do
              case "$f" in {{known}}) echo "  key $f ok" ;; *) fail "Unexpected key $f in nginx_signing.key." ;; esac
            done
            gpg --dearmor --yes -o /usr/share/keyrings/nginx-archive-keyring.gpg "$tmp"
            rm -f "$tmp"

            echo "> Adding $url ($codename)"
            echo "deb [signed-by=/usr/share/keyrings/nginx-archive-keyring.gpg] $url $codename nginx" > /etc/apt/sources.list.d/nginx.list
            printf 'Package: *\nPin: origin nginx.org\nPin: release o=nginx\nPin-Priority: 900\n' > /etc/apt/preferences.d/99nginx
            apt_update
            """;
        return RemoteScript.RunAsync(connection, script, log, "Adding the nginx.org repository failed", token);
    }

    // /etc/nginx is backed up to /root and then cleared so old site files cannot break the new configuration.
    public static Task RemoveDistroNginxAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """

            backup="/root/nginx-backup-$(date +%Y%m%d-%H%M%S).tar.gz"
            [ -d /etc/nginx ] && tar -czf "$backup" -C /etc nginx && echo "  Backup of /etc/nginx: $backup"
            echo "> Removing the Ubuntu/Debian NGINX packages"
            if [ -d /run/systemd/system ]; then systemctl stop nginx 2>/dev/null || true; else service nginx stop 2>/dev/null || true; fi
            pkgs=$(dpkg-query -W -f='${db:Status-Abbrev}${Package}\n' 'nginx*' 'libnginx-mod-*' 2>/dev/null | awk '/^ii/{print substr($0,4)}')
            [ -n "$pkgs" ] && apt-get purge -y $pkgs
            rm -rf /etc/nginx
            """;
        return RemoteScript.RunAsync(connection, script, log, "Removing the old NGINX failed", token);
    }

    /// <summary>Installs or upgrades NGINX mainline and returns the installed version.</summary>
    public static async Task<string> InstallPackageAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """

            echo "> apt-get install nginx"
            # confold: an upgrade keeps your own changes to nginx.conf
            apt-get install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold nginx
            version=$(dpkg-query -W -f='${Version}' nginx)
            case "$version" in *"~$codename"*) ;; *) fail "Installed nginx $version does not come from nginx.org." ;; esac
            echo "VERSION=$version"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing NGINX failed", ["VERSION"], token);
        return result["VERSION"] ?? "";
    }

    // sites-available, sites-enabled, snippets and proxy_params.
    public static Task SetUpDebianLayoutAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """

            c=/etc/nginx
            echo "> Creating sites-available, sites-enabled and snippets"
            install -d -m 755 "$c/sites-available" "$c/sites-enabled" "$c/snippets"

            if ! grep -q 'sites-enabled' "$c/nginx.conf"; then
              grep -q 'include /etc/nginx/conf.d/\*.conf;' "$c/nginx.conf" || fail "nginx.conf has no 'include /etc/nginx/conf.d/*.conf;' line to add sites-enabled after."
              sed -i 's|^\([[:space:]]*\)include /etc/nginx/conf.d/\*\.conf;|&\n\1include /etc/nginx/sites-enabled/*;|' "$c/nginx.conf"
              echo "  nginx.conf now includes /etc/nginx/sites-enabled/*"
            fi

            # The nginx.org default site becomes sites-available/default, like on Debian.
            if [ -f "$c/conf.d/default.conf" ] && [ ! -e "$c/sites-available/default" ]; then
              mv "$c/conf.d/default.conf" "$c/sites-available/default"
              ln -sfn "$c/sites-available/default" "$c/sites-enabled/default"
              echo "  conf.d/default.conf moved to sites-available/default (enabled)"
            fi

            # Without IPv6 NGINX cannot open [::]:80 and would not start.
            if [ ! -f /proc/net/if_inet6 ] && [ -f "$c/sites-available/default" ] && grep -q 'listen[[:space:]]*\[::\]' "$c/sites-available/default"; then
              sed -i 's|^\([[:space:]]*\)listen[[:space:]]*\[::\]|\1# listen [::]|' "$c/sites-available/default"
              echo "  IPv6 is off on this server: the [::]:80 listen in the default site is disabled"
            fi

            if [ ! -f "$c/proxy_params" ]; then
              cat > "$c/proxy_params" <<'PARAMS'
            proxy_set_header Host $http_host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            PARAMS
              echo "  proxy_params created"
            fi
            ls -l "$c"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Setting up the Debian layout failed", token);
    }

    // Also opens 80/443 when ufw is active.
    public static Task StartAndTestAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """

            echo "> nginx -t"
            nginx -t
            echo "> Starting NGINX"
            if [ -d /run/systemd/system ]; then
              systemctl enable nginx
              systemctl restart nginx
              systemctl is-active nginx
            else
              service nginx restart || nginx
            fi
            ufw_allow 80/tcp 443/tcp
            # Behind Traefik (web access step) NGINX serves the hotel on 127.0.0.1:85 instead of port 80.
            url=http://127.0.0.1/
            [ -L /etc/nginx/sites-enabled/hotel ] && grep -qs 'listen 127.0.0.1:85' /etc/nginx/sites-enabled/hotel && url=http://127.0.0.1:85/
            echo "> Requesting $url"
            code=$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)
            echo "  HTTP status: $code"
            [ "$code" = 200 ] || fail "NGINX is running but $url answered '$code' instead of 200."
            nginx -v
            """;
        return RemoteScript.RunAsync(connection, script, log, "Starting NGINX failed", token);
    }
}
