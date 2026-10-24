using AllInOneInstaller.Configuration;

namespace AllInOneInstaller.Remote;

/// <summary>AtomCMS state before the step runs.</summary>
public sealed record AtomCmsState(
    WebAccessSettings? Site,
    string? PhpVersion,
    bool ComposerPresent,
    string? NodeVersion,
    bool OctaneBuilt,
    bool EmulatorConfigured,
    string? HotelName,
    bool VendorPresent,
    bool ThemeBuilt,
    bool Migrated,
    bool SiteServesAtom,
    string? InstallationKey,
    bool InstallationCompleted)
{
    public bool IsComplete => Site is { AtomCms: true } && VendorPresent && ThemeBuilt && Migrated && SiteServesAtom;
}

/// <summary>
/// Installs AtomCMS (github.com/ObjectRetros/atomcms) as the hotel's website: the CMS takes the site root and
/// Octane moves to /client/, as in duckietm/Complete-Retro-on-Ubuntu's NGINX_Atom_setup.md.
/// </summary>
public static class AtomCmsInstaller
{
    public const string Repository = "https://github.com/ObjectRetros/atomcms.git";
    public const string Directory = "/var/www/atomcms";
    public const string Public = Directory + "/public";

    // Node.js 26 from /usr/bin; composer and artisan without prompts.
    private const string Environment =
        "export PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/root CI=1 NO_COLOR=1 COMPOSER_ALLOW_SUPERUSER=1 COMPOSER_NO_INTERACTION=1 COMPOSER_PROCESS_TIMEOUT=1800";

    // artisan as the PHP-FPM user, so the caches and logs it writes stay writable for the site.
    private const string Artisan = "runuser -u www-data -- php artisan";

    /// <summary>
    /// website_settings that point the CMS at this server's layout (also what its web installer asks for);
    /// rcon_port is copied from the emulator's config.ini.
    /// </summary>
    public static (string Key, string Value)[] WebsiteSettings(string hotelName) =>
    [
        ("hotel_name", hotelName),
        ("theme", "atom"),
        ("nitro_path", "/client"),
        ("nitro_external_texts_file", OctaneInstaller.Gamedata + "/config/ExternalTexts.json"),
        ("badges_path", "/gamedata/c_images/album1584"),
        ("badge_path_filesystem", OctaneInstaller.Gamedata + "/c_images/album1584"),
        ("group_badge_path", "/gamedata/c_images/groupbadge/badge"),
        ("furniture_icons_path", "/gamedata/icons"),
        ("ads_picture_path", "/gamedata/custom"),
        ("ads_path_filesystem", OctaneInstaller.Gamedata + "/custom"),
        ("rcon_ip", "127.0.0.1")
    ];

    public static async Task<AtomCmsState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            {{Environment}}
            [ -f {{WebAccessInstaller.TraefikDirectory}}/hotel.env ] && sed 's/^/conf_/' {{WebAccessInstaller.TraefikDirectory}}/hotel.env
            echo "php=$(php8.5 -r 'echo PHP_VERSION;' 2>/dev/null)"
            echo "composer=$(command -v composer >/dev/null && echo yes)"
            echo "node=$(node -v 2>/dev/null)"
            echo "octane=$([ -f {{OctaneInstaller.Dist}}/index.html ] && echo yes)"
            db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2)
            echo "emulator=$([ -n "$db" ] && echo yes)"
            d={{Directory}}
            echo "name=$(grep -sE '^APP_NAME=' "$d/.env" | head -1 | cut -d= -f2- | sed -E 's/^"(.*)"$/\1/')"
            echo "vendor=$([ -f "$d/vendor/autoload.php" ] && echo yes)"
            echo "theme=$([ -f "$d/public/build/manifest.json" ] && echo yes)"
            dbname=$(grep -sE '^DB_DATABASE=' "$d/.env" | cut -d= -f2)
            if [ -n "$dbname" ]; then
              echo "migrated=$(mariadb -N "$dbname" -e "SELECT 1 FROM website_installation LIMIT 1" >/dev/null 2>&1 && echo yes)"
              echo "key=$(mariadb -N "$dbname" -e "SELECT installation_key FROM website_installation ORDER BY id LIMIT 1" 2>/dev/null)"
              echo "completed=$(mariadb -N "$dbname" -e "SELECT 1 FROM website_installation WHERE completed = 1 LIMIT 1" 2>/dev/null)"
            fi
            echo "site=$(grep -qs 'root {{Public}};' {{HotelSite.File}} && echo yes)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);
        return new AtomCmsState(WebAccessSettings.FromHotelEnv(values), values["php"], values.Yes("composer"), values["node"],
            values.Yes("octane"), values.Yes("emulator"), values["name"], values.Yes("vendor"), values.Yes("theme"),
            values.Yes("migrated"), values.Yes("site"), values["key"], values["completed"] == "1");
    }

    // A .env value: double quotes, with what phpdotenv would expand or end the string escaped.
    private static string EnvValue(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";

    /// <summary>
    /// Creates .env from .env.example when it is missing and sets the keys this server decides. Without saved
    /// credentials the database login already in .env is kept.
    /// </summary>
    public static Task ConfigureEnvAsync(ServerConnection connection, WebAccessSettings site, string hotelName,
        DatabaseCredentials? credentials, Action<string> log, CancellationToken token = default)
    {
        string host = HardenInstaller.WhitelistHost(site);
        var values = new List<(string Key, string Value)>
        {
            ("APP_NAME", EnvValue(hotelName)),
            ("APP_ENV", "production"),
            ("APP_DEBUG", "false"),
            ("APP_URL", site.Url),
            ("LOG_LEVEL", "warning"),
            ("DB_CONNECTION", "mariadb"),
            ("DB_HOST", "127.0.0.1"),
            ("DB_PORT", "3306"),
            ("FORCE_HTTPS", site.Https ? "true" : "false"),
            ("SESSION_SECURE_COOKIE", site.Https ? "true" : "false"),
            ("SANCTUM_STATEFUL_DOMAINS", site.IsIpAddress ? site.Address : $"{host},www.{host}"),
            ("RCON_HOST", "127.0.0.1")
        };
        if (credentials is not null)
        {
            values.Add(("DB_DATABASE", credentials.Database));
            values.Add(("DB_USERNAME", credentials.CmsUser));
            values.Add(("DB_PASSWORD", EnvValue(credentials.CmsPassword)));
        }
        string sets = string.Join('\n', values.Select(v => $"env_set {v.Key} {ServerConnection.ShellQuote(v.Value)}"));

        string script = RemoteScript.Prelude + $$"""
            cd {{Directory}}
            if [ -f .env ]; then
              cp .env ".env.bak-$(date +%Y%m%d-%H%M%S)"
              echo "> {{Directory}}/.env (the old one is kept as .env.bak-<time>)"
            else
              cp .env.example .env
              echo "> {{Directory}}/.env from .env.example"
            fi
            env_set() {
              if grep -q "^$1=" .env; then
                KEY="$1" VALUE="$2" awk 'index($0, ENVIRON["KEY"] "=") == 1 { print ENVIRON["KEY"] "=" ENVIRON["VALUE"]; next } { print }' .env > .env.new
                cat .env.new > .env && rm -f .env.new
              else
                printf '%s=%s\n' "$1" "$2" >> .env
              fi
              case "$1" in *PASSWORD*) echo "  $1=***" ;; *) echo "  $1=$2" ;; esac
            }
            {{sets}}
            rcon=$(grep -sE '^rcon.port=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2 | tr -d '[:space:]')
            env_set RCON_PORT "${rcon:-3001}"
            grep -qE '^DB_PASSWORD=.+' .env && ! grep -qE '^DB_USERNAME=atomcms$' .env ||
              fail "The database login for AtomCMS is unknown: the MariaDB passwords of this server are not saved on this computer (configuration/database/<server>/mariadb.txt). Run the MariaDB step from this computer first."
            chmod 640 .env
            chown www-data:www-data .env
            """;
        return RemoteScript.RunAsync(connection, script, log, "Writing AtomCMS' .env failed", token);
    }

    /// <summary>composer install (with the dev packages: the seeder uses Faker for the first article's author).</summary>
    public static Task ComposerInstallAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            set -o pipefail
            command -v composer >/dev/null || fail "Composer is not installed. Run the Dev tools step first."
            cd {{Directory}}
            echo "> composer install   ($(composer --version 2>/dev/null | awk '{print "Composer " $3}'), PHP $(php -r 'echo PHP_VERSION;'))"
            rc=$(mktemp)
            (composer install --no-interaction --no-progress --optimize-autoloader 2>&1; echo $? > "$rc") \
              | grep -vE '^  - (Downloading|Installing|Syncing) |^    (Failed to download|Now trying)|^\s*$' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "composer install stopped with exit code $code (see above)."
            [ -f vendor/autoload.php ] || fail "composer install did not create vendor/autoload.php."
            echo "  $(ls vendor | wc -l) vendors installed"
            chown -R www-data:www-data {{Directory}}
            chmod -R ug+rwX storage bootstrap/cache
            """;
        return RemoteScript.RunAsync(connection, script, log, "composer install failed", token);
    }

    /// <summary>
    /// key:generate (once), storage:link and migrate; on the first install also the seeders and the website
    /// settings. Returns whether this was the first install.
    /// </summary>
    public static async Task<bool> MigrateAsync(ServerConnection connection, string hotelName,
        Action<string> log, CancellationToken token = default)
    {
        string settings = string.Join('\n', WebsiteSettings(hotelName).Select(s => $"UPDATE website_settings SET `value` = {Sql.Literal(s.Value)} WHERE `key` = {Sql.Literal(s.Key)};"));
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            set -o pipefail
            cd {{Directory}}
            db=$(grep -E '^DB_DATABASE=' .env | cut -d= -f2)
            [ -n "$db" ] || fail "DB_DATABASE is not set in {{Directory}}/.env."
            mariadb -N "$db" -e 'SELECT 1' >/dev/null 2>&1 || fail "Database $db does not exist (run the MariaDB step first)."

            if ! grep -qE '^APP_KEY=.+' .env; then
              echo "> php artisan key:generate"
              {{Artisan}} key:generate --force --no-interaction 2>&1 | sed 's/^ */  /'
            fi
            echo "> php artisan storage:link"
            {{Artisan}} storage:link --force --no-interaction 2>&1 | sed 's/^ */  /' || true

            first=yes
            mariadb -N "$db" -e "SELECT 1 FROM website_installation LIMIT 1" >/dev/null 2>&1 && first=
            if [ -n "$first" ]; then
              echo "> php artisan migrate --seed   (first install: AtomCMS' website_* tables next to the emulator's)"
              seed=--seed
            else
              echo "> php artisan migrate   (AtomCMS is already installed: only new migrations)"
              seed=
            fi
            rc=$(mktemp)
            ({{Artisan}} migrate --force --no-interaction $seed 2>&1; echo $? > "$rc") | grep -vE '^\s*$| RUNNING\s*$' | sed 's/^ */  /' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "php artisan migrate stopped with exit code $code (see above)."

            if [ -n "$first" ]; then
              rcon=$(grep -sE '^rcon.port=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2 | tr -d '[:space:]')
              echo "> website_settings: hotel name, theme atom, nitro_path /client and the gamedata paths"
              mariadb "$db" <<'SQL'
            {{settings}}
            SQL
              mariadb "$db" -e "UPDATE website_settings SET \`value\` = '${rcon:-3001}' WHERE \`key\` = 'rcon_port'"
              mariadb -N "$db" -e "SELECT CONCAT('  ', \`key\`, ' = ', \`value\`) FROM website_settings WHERE \`key\` IN ('hotel_name', 'theme', 'nitro_path', 'badges_path', 'group_badge_path', 'furniture_icons_path', 'rcon_port')"
            fi
            {{Artisan}} optimize:clear --no-interaction >/dev/null 2>&1 || true
            echo "FIRST=$first"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Setting up AtomCMS' database failed",
            ["FIRST"], token);
        return result.Yes("FIRST");
    }

    /// <summary>
    /// The key AtomCMS' web installer asks for. AtomCMS creates it on the first visit, so this opens /installation
    /// through Traefik; if there is still no key, it is added to website_installation directly.
    /// </summary>
    public static async Task<(string? Key, bool Completed)> InstallationKeyAsync(ServerConnection connection, WebAccessSettings site,
        Action<string> log, CancellationToken token = default)
    {
        string name = site.IsIpAddress ? "127.0.0.1" : site.Address;
        string port = site.Https ? "443" : "80";
        string script = RemoteScript.Prelude + $$"""
            cd {{Directory}}
            db=$(grep -E '^DB_DATABASE=' .env | cut -d= -f2)
            key() { mariadb -N "$db" -e "SELECT installation_key FROM website_installation ORDER BY id LIMIT 1" 2>/dev/null; }
            url="{{site.Scheme}}://{{name}}"
            # AtomCMS remembers "installed" in its cache; without a finished installation in the database that
            # would hide the web installer.
            if ! mariadb -N "$db" -e "SELECT 1 FROM website_installation WHERE completed = 1" 2>/dev/null | grep -q 1; then
              {{Artisan}} cache:forget app_installed >/dev/null 2>&1 || true
              {{Artisan}} cache:forget installation_record >/dev/null 2>&1 || true
            fi
            if [ -z "$(key)" ]; then
              echo "> $url/installation (AtomCMS creates its installation key on the first visit)"
              curl -s --noproxy '*' -k --max-time 20 --resolve "{{name}}:{{port}}:127.0.0.1" -o /dev/null -w '  HTTP %{http_code}\n' "$url/installation" || true
            fi
            if [ -z "$(key)" ]; then
              echo "  No key yet: adding one to website_installation"
              mariadb "$db" -e "INSERT INTO website_installation (step, completed, installation_key, created_at, updated_at)
                SELECT 0, 0, UUID(), NOW(), NOW() FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM website_installation)"
            fi
            k=$(key)
            [ -n "$k" ] || fail "AtomCMS has no installation key (SELECT installation_key FROM website_installation; is empty)."
            completed=$(mariadb -N "$db" -e "SELECT 1 FROM website_installation WHERE completed = 1 LIMIT 1" 2>/dev/null)
            echo ""
            if [ "$completed" = 1 ]; then
              echo "  AtomCMS' web installer is already finished (installation key $k)."
            else
              echo "  ============================================================"
              echo "  AtomCMS installation key:  $k"
              echo "  Open $url and paste it in the web installer."
              echo "  ============================================================"
            fi
            echo "KEY=$k"
            echo "COMPLETED=$completed"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Reading AtomCMS' installation key failed",
            ["KEY", "COMPLETED"], token);
        return (result["KEY"], result["COMPLETED"] == "1");
    }

    /// <summary>npm ci (package-lock.json) and npm run build:atom, which also builds the housekeeping theme.</summary>
    public static Task BuildThemeAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            set -o pipefail
            command -v node >/dev/null && command -v npm >/dev/null || fail "Node.js is missing in /usr/bin. Run the Dev tools step first."
            cd {{Directory}}
            echo "> npm ci   (node $(node -v), npm $(npm -v))"
            rc=$(mktemp)
            (npm ci --no-audit --no-fund 2>&1; echo $? > "$rc") | grep -vE '^npm (warn|WARN) (deprecated|EBADENGINE)|^\s*$' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "npm ci stopped with exit code $code (see above)."
            echo "> npm run build:atom"
            (npm run build:atom </dev/null 2>&1; echo $? > "$rc") | grep -vE '^public/build|^\s*$|computing gzip|transforming|rendering chunks' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "npm run build:atom stopped with exit code $code (see above)."
            echo "  public/build $(du -sh public/build | cut -f1), public/build-housekeeping $(du -sh public/build-housekeeping 2>/dev/null | cut -f1)"
            chown -R www-data:www-data public/build public/build-housekeeping 2>/dev/null || true
            """;
        return RemoteScript.RunAsync(connection, script, log, "Building the AtomCMS theme failed", token);
    }

    /// <summary>
    /// Through Traefik: AtomCMS on the site, Octane on /client/, Polaris on /api/health and AtomCMS' own /api.
    /// </summary>
    public static Task TestAsync(ServerConnection connection, WebAccessSettings site, Action<string> log, CancellationToken token = default)
    {
        string name = site.IsIpAddress ? "127.0.0.1" : site.Address;
        string port = site.Https ? "443" : "80";
        string script = RemoteScript.Prelude + $$"""
            url="{{site.Scheme}}://{{name}}"
            c() { curl -s --noproxy '*' -k --max-time 15 --resolve "{{name}}:{{port}}:127.0.0.1" "$@"; }
            echo "> $url/ (AtomCMS)"
            code=$(c -o /dev/null -w '%{http_code} %{redirect_url}' "$url/" || true)
            echo "  HTTP $code"
            case "$code" in 200*|302*) ;; *) tail -5 {{Directory}}/storage/logs/laravel.log 2>/dev/null | cut -c1-300 | sed 's/^/  /'; fail "AtomCMS does not answer on $url/ (HTTP '$code')." ;; esac
            echo "> $url/client/ (Octane)"
            c "$url/client/" | grep -q 'configuration/bootstrap.js' || fail "$url/client/ does not serve Octane."
            echo "  index.html OK"
            echo "> $url/api/online-count (AtomCMS' API)"
            echo "  HTTP $(c -o /dev/null -w '%{http_code}' "$url/api/online-count" || true)"
            echo "> $url/api/health (Polaris)"
            echo "  HTTP $(c -o /dev/null -w '%{http_code}' "$url/api/health" || true)"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Testing AtomCMS failed", token);
    }
}
