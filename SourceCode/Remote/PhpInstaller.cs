namespace AllInOneInstaller.Remote;

/// <summary>PHP as found on the server before the PHP step runs.</summary>
public sealed record PhpState(
    string Codename,
    string? PhpVersion,
    IReadOnlyList<string> MissingPackages,
    IReadOnlyList<string> OtherVersions,
    bool RepositoryConfigured,
    bool FpmRunning)
{
    public bool VersionOk => PhpVersion is not null && PhpInstaller.IsAtLeastMinimum(PhpVersion);

    public bool IsComplete => MissingPackages.Count == 0 && VersionOk && FpmRunning;
}

/// <summary>Installs PHP 8.5 with PHP-FPM from packages.sury.org and connects PHP-FPM to NGINX.</summary>
public static class PhpInstaller
{
    public const string Branch = "8.5";

    // Older 8.5 releases have known security problems.
    public const string MinimumVersion = "8.5.12";

    // DEB.SURY.ORG Automatic Signing Key, shipped in debsuryorg-archive-keyring.deb.
    public const string SuryKeyFingerprint = "15058500A0235D97F5D10063B188E2B695BD4743";

    public static readonly string[] Packages =
    [
        "php8.5-fpm", "php8.5", "php8.5-common", "php8.5-mysql",
        "php8.5-xml", "php8.5-xmlrpc", "php8.5-curl", "php8.5-gd",
        "php8.5-imagick", "php8.5-cli", "php8.5-mbstring",
        "php8.5-soap", "php8.5-zip", "php8.5-intl", "php8.5-bcmath", "unzip"
    ];

    // Lower-case names as listed by "php -m".
    private static readonly string[] Extensions =
        ["mysqli", "pdo_mysql", "xml", "xmlrpc", "curl", "gd", "imagick", "mbstring", "soap", "zip", "intl", "bcmath"];

    private const string Socket = "/run/php/php8.5-fpm.sock";


    // Only the numeric x.y.z prefix is compared.
    public static bool IsAtLeastMinimum(string phpVersion)
    {
        string numeric = new(phpVersion.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(numeric, out Version? version) && version >= Version.Parse(MinimumVersion);
    }

    public static async Task<PhpState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = """
            . /etc/os-release
            echo "codename=${VERSION_CODENAME:-$(lsb_release -cs 2>/dev/null)}"
            echo "php=$(command -v php8.5 >/dev/null 2>&1 && php8.5 -r 'echo PHP_VERSION;')"
            missing=""
            for p in PACKAGES; do
              dpkg-query -W -f='${db:Status-Abbrev}' "$p" 2>/dev/null | grep -q '^ii' || missing="$missing $p"
            done
            echo "missing=$missing"
            echo "others=$(ls /etc/php 2>/dev/null | grep -vx '8.5' | tr '\n' ' ')"
            echo "repo=$(grep -qs 'packages.sury.org/php' /etc/apt/sources.list.d/*.list && echo yes)"
            # [r] keeps pgrep from matching this script's own command line
            echo "fpm=$(pgrep -f 'php-fpm: maste[r] process \(/etc/php/8\.5/' >/dev/null && echo yes)"
            """.Replace("PACKAGES", string.Join(' ', Packages));
        CommandResult result = await connection.RunAsRootAsync(script, token);
        if (!result.Succeeded) throw new ServerConnectionException("Could not inspect PHP on the server: " + result.Error.Trim());

        var values = new ScriptValues(result.Output);
        string[] List(string key) => (values[key] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new PhpState(values["codename"] ?? "", values["php"], List("missing"), List("others"), values.Yes("repo"), values.Yes("fpm"));
    }

    // Follows packages.sury.org/php/README.txt, but checks the keyring before installing it.
    public static Task AddRepositoryAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get update"
            apt_update
            echo "> Installing prerequisites"
            apt-get install -y --no-install-recommends lsb-release ca-certificates curl gnupg
            curl -fsSI "https://packages.sury.org/php/dists/$codename/Release" >/dev/null || fail "packages.sury.org has no PHP packages for $PRETTY_NAME ($codename)."

            echo "> Downloading and checking the Sury keyring"
            tmp=$(mktemp -d)
            trap 'rm -rf "$tmp"' EXIT
            curl -fsSL -o "$tmp/keyring.deb" https://packages.sury.org/debsuryorg-archive-keyring.deb || fail "Could not download debsuryorg-archive-keyring.deb."
            [ "$(dpkg-deb -f "$tmp/keyring.deb" Package)" = debsuryorg-archive-keyring ] || fail "The downloaded file is not the debsuryorg-archive-keyring package."
            dpkg-deb -x "$tmp/keyring.deb" "$tmp/x"
            fprs=$(gpg --show-keys --with-colons "$tmp/x/usr/share/keyrings/debsuryorg-archive-keyring.gpg" 2>/dev/null | awk -F: '/^pub/{p=1; next} /^fpr/ && p {print $10; p=0}')
            [ "$fprs" = "{{SuryKeyFingerprint}}" ] || fail "The Sury keyring does not hold the expected key {{SuryKeyFingerprint}} (found: $fprs)."
            echo "  key {{SuryKeyFingerprint}} ok"
            dpkg -i "$tmp/keyring.deb"

            echo "> Adding https://packages.sury.org/php/ ($codename)"
            echo "deb [signed-by=/usr/share/keyrings/debsuryorg-archive-keyring.gpg] https://packages.sury.org/php/ $codename main" > /etc/apt/sources.list.d/php.list
            # PHP 8.5 always from Sury, also where the distribution has its own (older) 8.5 packages.
            printf 'Package: php8.5*\nPin: origin packages.sury.org\nPin-Priority: 900\n' > /etc/apt/preferences.d/99sury-php
            apt_update
            """;
        return RemoteScript.RunAsync(connection, script, log, "Adding the PHP repository failed", token);
    }

    /// <summary>Fails on a PHP older than <see cref="MinimumVersion"/>; returns the installed version.</summary>
    public static async Task<string> InstallPackagesAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get install {{string.Join(' ', Packages)}}"
            apt-get install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold {{string.Join(' ', Packages)}}
            v=$(php8.5 -r 'echo PHP_VERSION;')
            dpkg --compare-versions "$v" ge {{MinimumVersion}} || fail "PHP $v is installed, but at least {{MinimumVersion}} is required: older versions have security problems."
            # 'php' on the command line is 8.5, also when other versions are installed.
            update-alternatives --set php /usr/bin/php8.5 >/dev/null 2>&1 || true
            echo "VERSION=$v"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing PHP failed", ["VERSION"], token);
        return result["VERSION"] ?? "";
    }

    public static Task CheckExtensionsAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> php8.5 -m"
            mods=$(php8.5 -m | tr 'A-Z' 'a-z')
            missing=""
            for m in {{string.Join(' ', Extensions)}}; do
              if echo "$mods" | grep -qx "$m"; then echo "  $m ok"; else missing="$missing $m"; fi
            done
            [ -z "$missing" ] || fail "These PHP extensions are not loaded:$missing"
            echo "> php-fpm8.5 -t"
            php-fpm8.5 -t
            """;
        return RemoteScript.RunAsync(connection, script, log, "Checking PHP failed", token);
    }

    /// <summary>
    /// Starts PHP-FPM and, if NGINX is installed, wires it up and tests a real PHP request through it.
    /// Returns whether NGINX was present.
    /// </summary>
    public static async Task<bool> StartAndConnectAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> Starting PHP-FPM"
            if [ -d /run/systemd/system ]; then
              systemctl enable php8.5-fpm
              systemctl restart php8.5-fpm
            else
              service php8.5-fpm restart
            fi
            for i in 1 2 3 4 5 6 7 8 9 10; do [ -S {{Socket}} ] && break; sleep 0.5; done
            [ -S {{Socket}} ] || fail "PHP-FPM is not listening on {{Socket}}."
            echo "  listening on {{Socket}}"

            if ! command -v nginx >/dev/null 2>&1; then
              echo "  NGINX is not installed: skipping the NGINX + PHP test"
              echo "NGINX=no"
              exit 0
            fi

            # nginx.org's NGINX runs as 'nginx'; the PHP-FPM socket belongs to www-data (mode 660).
            if id nginx >/dev/null 2>&1 && ! id -nG nginx | grep -qw www-data; then
              usermod -aG www-data nginx
              echo "  user nginx added to group www-data (for the PHP-FPM socket)"
            fi

            install -d -m 755 /etc/nginx/snippets
            if [ ! -f /etc/nginx/snippets/fastcgi-php.conf ]; then
              cat > /etc/nginx/snippets/fastcgi-php.conf <<'SNIPPET'
            # regex to split $uri to $fastcgi_script_name and $fastcgi_path
            fastcgi_split_path_info ^(.+?\.php)(/.*)$;

            # Check that the PHP script exists before passing it
            try_files $fastcgi_script_name =404;

            # Bypass the fact that try_files resets $fastcgi_path_info
            set $path_info $fastcgi_path_info;
            fastcgi_param PATH_INFO $path_info;

            fastcgi_index index.php;
            include fastcgi_params;
            fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
            SNIPPET
              echo "  /etc/nginx/snippets/fastcgi-php.conf created"
            fi

            reload() {
              if [ -d /run/systemd/system ]; then systemctl reload nginx; else nginx -s reload 2>/dev/null || nginx; fi
            }

            echo "> Testing PHP through NGINX"
            # Temporary site on 127.0.0.1:8085; PHP-FPM has a private /tmp, so the test file lives in /var/www.
            root=$(mktemp -d /var/www/aio-php-test.XXXXXX 2>/dev/null || { install -d -m 755 /var/www; mktemp -d /var/www/aio-php-test.XXXXXX; })
            conf=/etc/nginx/conf.d/zz-aio-php-test.conf
            cleanup() { rm -rf "$root"; rm -f "$conf"; reload >/dev/null 2>&1 || true; }
            trap cleanup EXIT
            chmod 755 "$root"
            echo '<?php echo "PHP-OK " . PHP_VERSION;' > "$root/index.php"
            chmod 644 "$root/index.php"
            cat > "$conf" <<SITE
            server {
                listen 127.0.0.1:8085;
                root $root;
                location ~ \.php\$ {
                    include snippets/fastcgi-php.conf;
                    fastcgi_pass unix:{{Socket}};
                }
            }
            SITE
            nginx -t
            reload
            sleep 1
            answer=$(curl -s --noproxy '*' http://127.0.0.1:8085/index.php || true)
            echo "  answer: $answer"
            case "$answer" in "PHP-OK "*) ;; *) fail "NGINX could not run PHP through PHP-FPM (answer: '$answer')." ;; esac
            echo "NGINX=yes"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Starting PHP-FPM failed", ["NGINX"], token);
        return result.Yes("NGINX");
    }
}
