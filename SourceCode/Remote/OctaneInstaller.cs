namespace AllInOneInstaller.Remote;

/// <summary>Octane state before the step runs.</summary>
public sealed record OctaneState(
    WebAccessSettings? Site,
    bool OctanePresent,
    bool RendererPresent,
    string? NodeVersion,
    string? ConfiguredSocketUrl,
    bool Built,
    bool SiteServesOctane,
    bool GamedataPresent,
    string? UiCameraUrl,
    string? EmulatorImager,
    string? ConfigBaseUrl)
{
    public string? ExpectedSocketUrl => Site is null ? null : OctaneInstaller.SocketUrl(Site);

    /// <summary>Both ui-config.json and the emulator's imager settings point at gamedata/camera.</summary>
    public bool CameraConfigured => Site is not null && UiCameraUrl == $"{Site.Url}/gamedata/camera" &&
                                    EmulatorImager == EmulatorInstaller.ImagerSettingsLine(Site.Url);

    /// <summary>client-mode.json follows the site layout: /configuration/, or /client/configuration/ with AtomCMS.</summary>
    public bool LayoutConfigured => Site is not null && ConfigBaseUrl == $"{Site.ClientUrl}configuration/";

    public bool IsComplete => Site is not null && Built && SiteServesOctane && GamedataPresent && ConfiguredSocketUrl == ExpectedSocketUrl &&
                              CameraConfigured && LayoutConfigured;
}

/// <summary>Configures, builds and publishes the Octane client.</summary>
public static class OctaneInstaller
{
    public const string Octane = "/var/www/Octane";
    public const string Renderer = "/var/www/Octane-Renderer";
    public const string Gamedata = "/var/www/gamedata";
    public const string Dist = Octane + "/dist";
    public const string ConfigurationDirectory = Octane + "/public/configuration";

    /// <summary>Files the manual install renames from .example to .json.</summary>
    public static readonly string[] ConfigurationFiles =
        ["client-mode", "hotlooks", "infostand_backgrounds", "news", "renderer-config", "ui-config"];

    // Use Node.js 26 from /usr/bin, never an older copy in /usr/local/bin.
    private const string Environment =
        "export PATH=/usr/sbin:/usr/bin:/sbin:/bin COREPACK_ENABLE_DOWNLOAD_PROMPT=0 CI=1 NO_COLOR=1 FORCE_COLOR=0 HOME=/root";

    /// <summary>
    /// Sets or removes vite's --base in the start and build scripts of package.json (the AtomCMS guide's change),
    /// e.g. "vite build" becomes "vite --base=/client/ build". The Octane clone step undoes it before a git pull.
    /// </summary>
    public static string PackageBaseCommand(WebAccessSettings site) =>
        "sed -i -E -e 's#(\"(start|build)\": \"vite) --base=[^ \"]+#\\1#'" +
        (site.AtomCms ? $" -e 's#(\"(start|build)\": \"vite)( |\")#\\1 --base={site.ClientPath}\\3#'" : "") + " package.json";

    public static string SocketUrl(WebAccessSettings site) => $"{(site.Https ? "wss" : "ws")}://{site.Url[(site.Url.IndexOf("://", StringComparison.Ordinal) + 3)..]}/ws";

    public static async Task<OctaneState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            {{Environment}}
            [ -f {{WebAccessInstaller.TraefikDirectory}}/hotel.env ] && sed 's/^/conf_/' {{WebAccessInstaller.TraefikDirectory}}/hotel.env
            echo "octane=$([ -f {{Octane}}/package.json ] && echo yes)"
            echo "renderer=$([ -f {{Renderer}}/package.json ] && echo yes)"
            echo "node=$(node -v 2>/dev/null)"
            echo "socket=$(node -e 'try { console.log(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))["socket.url"] || "") } catch { }' {{ConfigurationDirectory}}/renderer-config.json 2>/dev/null)"
            echo "built=$([ -f {{Dist}}/index.html ] && echo yes)"
            echo "site=$(grep -Eqs '(root|alias) {{Dist}}/?;' {{WebAccessInstaller.SiteFile}} && echo yes)"
            echo "configbase=$(node -e 'try { console.log(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))["plainConfigBaseUrl"] || "") } catch { }' {{ConfigurationDirectory}}/client-mode.json 2>/dev/null)"
            echo "gamedata=$([ -d {{Gamedata}}/config ] && echo yes)"
            echo "uicamera=$(node -e 'try { console.log(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))["camera.url"] || "") } catch { }' {{ConfigurationDirectory}}/ui-config.json 2>/dev/null)"
            db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2)
            [ -n "$db" ] && echo "emuimager=$(mariadb -N "$db" -e "SELECT GROUP_CONCAT(\`key\`, '=', \`value\` ORDER BY \`key\` SEPARATOR ',') FROM emulator_settings WHERE \`key\` IN ({{EmulatorInstaller.ImagerKeysSql}})" 2>/dev/null)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);

        WebAccessSettings? site = WebAccessSettings.FromHotelEnv(values);
        return new OctaneState(site, values.Yes("octane"), values.Yes("renderer"), values["node"],
            values["socket"], values.Yes("built"), values.Yes("site"), values.Yes("gamedata"),
            values["uicamera"], values["emuimager"], values["configbase"]);
    }

    /// <summary>
    /// Uses Yarn 4 through Corepack, as package.json pins. <paramref name="fresh"/> removes node_modules and
    /// yarn.lock first, so every package is resolved again (the update tool).
    /// </summary>
    public static Task YarnInstallAsync(ServerConnection connection, string directory, Action<string> log, CancellationToken token = default,
        bool fresh = false)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            set -o pipefail
            command -v node >/dev/null && command -v yarn >/dev/null || fail "Node.js and Yarn are missing in /usr/bin. Run the Node.js step first."
            cd {{directory}}
            {{(fresh ? """
            echo "> rm -rf node_modules yarn.lock"
            rm -rf node_modules yarn.lock
            # CI mode makes Yarn refuse to write a lockfile; here a new one is the point.
            export YARN_ENABLE_IMMUTABLE_INSTALLS=false
            """ : "")}}
            echo "> cd {{directory}} && yarn install   (node $(node -v), yarn $(yarn --version))"
            # Filter Yarn's step frames, but keep its exit code.
            rc=$(mktemp)
            (yarn install 2>&1; echo $? > "$rc") | grep -vE '^➤ YN0000: [│┌└]|^➤ YN0007|^➤ YN0085' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "yarn install in {{directory}} stopped with exit code $code (see above)."
            """;
        return RemoteScript.RunAsync(connection, script, log, $"yarn install in {directory} failed", token);
    }

    /// <summary>
    /// Creates missing .json files from .example and sets the URL keys, backing up existing files first. Also
    /// creates the asset folders they reference.
    /// </summary>
    public static Task ConfigureAsync(ServerConnection connection, WebAccessSettings site, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            # The emulator's timezone (config.ini) for timezone.settings in a new renderer-config.json.
            tz=$(grep -oE 'serverTimezone=[^&[:space:]]+' {{EmulatorInstaller.Directory}}/config.ini 2>/dev/null | head -1 | cut -d= -f2)
            echo "> {{ConfigurationDirectory}}: {{string.Join(", ", ConfigurationFiles.Select(f => f + ".json"))}}"
            SITE_URL='{{site.Url}}' CLIENT_URL='{{site.ClientUrl}}' ATOM='{{(site.AtomCms ? "yes" : "")}}' SOCKET_URL='{{SocketUrl(site)}}' CONFIG_DIR='{{ConfigurationDirectory}}' GAMEDATA_DIR='{{Gamedata}}' \
              FILES='{{string.Join(' ', ConfigurationFiles)}}' TIMEZONE="$tz" node - <<'JS'
            {{ConfigureScript}}
            JS
            echo "> {{Octane}}/package.json: {{(site.AtomCms ? $"vite --base={site.ClientPath} (Octane on {site.ClientPath})" : "vite without --base (Octane on /)")}}"
            cd {{Octane}}
            {{PackageBaseCommand(site)}}
            grep -E '"(start|build)": ' package.json | sed 's/^ */  /'
            # The emulator (group www-data) writes into gamedata: custom badges (c_images/album1584) and edits of
            # config/*.json. Default ACLs keep files copied in later (download tool, WinSCP) writable for it too.
            command -v setfacl >/dev/null 2>&1 || { echo "> Installing acl (setfacl)"; apt_update >/dev/null; apt-get install -y acl >/dev/null; }
            cat > {{PermissionsScript}} <<'PERMS'
            {{PermissionsScriptContent}}
            PERMS
            chmod 755 {{PermissionsScript}}
            echo "> {{PermissionsScript}}"
            {{PermissionsScript}}

            # The login screen's news (login.news.url): a welcome message, never over the hotel's own news.
            install -d -m 2775 -o www-data -g www-data {{Gamedata}}/news
            if [ -f {{Gamedata}}/news/news.json ]; then
              echo "> {{Gamedata}}/news/news.json: already there, kept"
            else
              cat > {{Gamedata}}/news/news.json <<'NEWS'
            {{WelcomeNews(site)}}
            NEWS
              chown www-data:www-data {{Gamedata}}/news/news.json
              chmod 664 {{Gamedata}}/news/news.json
              echo "> {{Gamedata}}/news/news.json: welcome message created"
            fi
            """;
        return RemoteScript.RunAsync(connection, script, log, "Writing the Octane configuration failed", token);
    }

    /// <summary>Restores www-data read/write access to gamedata; rerun after copying assets in.</summary>
    public const string PermissionsScript = "/usr/local/sbin/gamedata-permissions";

    private const string PermissionsScriptContent = $$"""
        #!/bin/sh
        # Read/write access to {{Gamedata}} for the www-data group (written by All-in-1 Installer).
        # The emulator (a member of www-data) writes custom badges to c_images/album1584 and edits config/*.json;
        # NGINX reads everything. The default ACLs give files and folders created later the same rights, also when
        # they are copied in as root or another user. Run this again when an upload changed the permissions.
        set -e
        d={{Gamedata}}
        [ -d "$d" ] || exit 0
        chgrp -R www-data "$d"
        find "$d" -type d -exec chmod 2775 {} +
        find "$d" -type f -exec chmod ug+rw,o+r {} +
        setfacl -R -m g:www-data:rwX -m d:g:www-data:rwX -m d:u::rwX -m d:o::rX "$d"
        echo "  $d: group www-data can read and write (also new files: default ACL), others can read"
        """;

    /// <summary>Contents of /gamedata/news/news.json.</summary>
    public static string WelcomeNews(WebAccessSettings site) => WelcomeNewsTemplate.Replace("@ADDRESS@", site.Address);

    private const string WelcomeNewsTemplate = """
        {
            "news": [
                {
                    "id": 1,
                    "title": "Welcome to @ADDRESS@!",
                    "body": "The doors are open! Octane is the brand-new client of this hotel: create your avatar, decorate your own room, meet new friends and explore everything the hotel has to offer. Grab a drink in the lobby, say hello and make yourself at home. Have fun, and see you in the hotel!",
                    "image": "",
                    "link": "",
                    "linkText": "Read more"
                }
            ]
        }
        """;

    // Plain string, not $$: the JavaScript is full of braces.
    private const string ConfigureScript = """
        const fs = require('fs');
        const path = require('path');
        const { SITE_URL: site, CLIENT_URL: client, ATOM: atom, SOCKET_URL: socket, CONFIG_DIR: dir, GAMEDATA_DIR: gamedata, TIMEZONE: timezone } = process.env;

        // Always set: the hotel's URLs. Only in a file created from the .example: the folder layout and modes.
        const urls = {
            'renderer-config': {
                'socket.url': socket,
                'api.url': site,
                'asset.url': `${site}/gamedata`,
                'image.library.url': `${site}/gamedata/c_images/`,
                'hof.furni.url': site,
                'furni.asset.icon.url': `${site}/gamedata/icons/%libname%%param%_icon.png`,
                'badge.asset.group.url': `${site}/gamedata/c_images/groupbadge/badge/%badgedata%`,
                'badge.asset.grouparts.url': `${site}/gamedata/c_images/groupbadge/badgeparts/badgepart_%part%.png`,
                'group.badge.url': '%imagerdata%',
                // With AtomCMS the CMS logs in and opens the client with an SSO ticket.
                ...(atom ? { 'login.screen.enabled': false } : {})
            },
            'ui-config': {
                'camera.url': `${site}/gamedata/camera`,
                'thumbnails.url': `${site}/gamedata/camera/thumb/%thumbnail%.png`,
                'camera.render.mode': 'upload'
            },
            'client-mode': {
                'apiBaseUrl': site,
                'plainConfigBaseUrl': `${client}configuration/`,
                'plainGamedataBaseUrl': `${site}/gamedata/`
            }
        };
        const defaults = {
            'renderer-config': {
                ...(timezone ? { 'timezone.settings': timezone } : {})
            },
            'client-mode': { 'distObfuscationEnabled': false, 'secureAssetsEnabled': false, 'secureApiEnabled': false }
        };

        // JSON, or JSONC (comments, trailing commas) when the file was edited that way.
        const parse = (file, text) => {
            try { return JSON.parse(text); } catch { }
            const stripped = text
                .replace(/("(?:[^"\\]|\\.)*")|\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, (m, s) => s ?? '')
                .replace(/,(\s*[}\]])/g, '$1');
            try { return JSON.parse(stripped); } catch (e) { throw new Error(`${file} is not valid JSON(C): ${e.message}`); }
        };

        const configs = {};
        for (const name of process.env.FILES.split(' ')) {
            const file = path.join(dir, `${name}.json`);
            const example = path.join(dir, `${name}.example`);
            let created = false, config;
            if (fs.existsSync(file)) config = parse(file, fs.readFileSync(file, 'utf8'));
            else if (fs.existsSync(example)) { config = parse(example, fs.readFileSync(example, 'utf8')); created = true; }
            else { console.log(`  ${name}.json: no ${name}.example in this Octane version, skipped`); continue; }

            const changes = [];
            for (const [key, value] of Object.entries({ ...(created ? defaults[name] : {}), ...urls[name] })) {
                if (config[key] !== value) { changes.push(`${key} = ${JSON.stringify(value)}`); config[key] = value; }
            }
            if (created || changes.length > 0) {
                if (!created) fs.copyFileSync(file, `${file}.bak-${new Date().toISOString().replace(/\D/g, '').slice(0, 14)}`);
                fs.writeFileSync(file, JSON.stringify(config, null, 4) + '\n');
            }
            console.log(`  ${name}.json: ${created ? `created from ${name}.example` : changes.length ? 'updated (old file kept as .bak-<time>)' : 'already up to date'}`);
            for (const change of changes) console.log(`    ${change}`);
            configs[name] = config;
        }

        // Every folder the asset URLs point to, e.g. ${asset.url}/furniture/%libname%.hab -> furniture.
        const settings = { ...configs['renderer-config'], ...configs['ui-config'] };
        const expand = (value, depth = 0) => depth > 8 ? value
            : value.replace(/\$\{([^}]+)\}/g, (m, key) => typeof settings[key] === 'string' ? expand(settings[key], depth + 1) : m);
        const folders = new Set();
        const visit = value => {
            if (Array.isArray(value)) return value.forEach(visit);
            if (value && typeof value === 'object') return Object.values(value).forEach(visit);
            if (typeof value !== 'string' || !value) return;
            let url = expand(value);
            for (const prefix of [`${site}/gamedata/`, '/gamedata/']) {
                if (!url.startsWith(prefix)) continue;
                const parts = url.slice(prefix.length).split(/[?#]/)[0].split('/').filter(Boolean);
                const cut = parts.findIndex(p => p.includes('%') || p.includes('${'));
                const kept = cut < 0 ? parts : parts.slice(0, cut);
                if (cut < 0 && kept.length && kept[kept.length - 1].includes('.')) kept.pop();
                if (kept.length) folders.add(kept.join('/'));
                break;
            }
        };
        visit(settings);
        let made = 0;
        for (const folder of [...folders].sort()) {
            const full = path.join(gamedata, folder);
            if (!full.startsWith(gamedata + '/')) continue;
            if (!fs.existsSync(full)) { fs.mkdirSync(full, { recursive: true }); made++; }
        }
        fs.mkdirSync(gamedata, { recursive: true });
        console.log(`> ${gamedata}: ${folders.size} asset folders (${made} new): ${[...folders].sort().join(', ')}`);
        """;

    /// <summary>Writes build.sh for later rebuilds, then builds.</summary>
    public static Task BuildAsync(ServerConnection connection, WebAccessSettings site, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Environment}}
            set -o pipefail
            cat > {{Octane}}/build.sh <<'BUILD'
            #!/bin/sh
            # Builds the Octane client into dist/ (written by All-in-1 Installer). Run it after changing
            # public/configuration or after a git pull of Octane or Octane-Renderer.
            set -e
            export PATH=/usr/sbin:/usr/bin:/sbin:/bin COREPACK_ENABLE_DOWNLOAD_PROMPT=0 CI=1
            cd {{Renderer}} && yarn install
            cd {{Octane}}
            # Octane on {{site.ClientPath}}: keeps package.json's --base right after a git pull.
            {{PackageBaseCommand(site)}}
            yarn install && yarn build
            BUILD
            chmod 755 {{Octane}}/build.sh
            cd {{Octane}}
            echo "> cd {{Octane}} && yarn build"
            start=$(date +%s)
            rc=$(mktemp)
            (yarn build </dev/null 2>&1; echo $? > "$rc") | grep -vE '^dist/|^\s*$|computing gzip|transforming|rendering chunks' || true
            code=$(cat "$rc"); rm -f "$rc"
            [ "$code" = 0 ] || fail "yarn build stopped with exit code $code (see above)."
            [ -f {{Dist}}/index.html ] || fail "yarn build did not create {{Dist}}/index.html (see above)."
            echo "  dist/ is $(du -sh {{Dist}} | cut -f1), built in $(( $(date +%s) - start )) seconds"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Building Octane failed", token);
    }

    /// <summary>Points the hotel site at dist/ and checks it through Traefik.</summary>
    public static Task PublishAsync(ServerConnection connection, WebAccessSettings site, Action<string> log, CancellationToken token = default)
    {
        string name = site.IsIpAddress ? "127.0.0.1" : site.Address;
        string port = site.Https ? "443" : "80";
        string script = RemoteScript.Prelude + $$"""
            {{HotelSite.WriteScript(site, Dist)}}

            url="{{site.Scheme}}://{{name}}"
            c() { curl -s --noproxy '*' -k --max-time 10 --resolve "{{name}}:{{port}}:127.0.0.1" "$@"; }
            client="$url{{site.ClientPath}}"
            echo "> $client (Octane)"
            # The NGINX reload above finishes in the background: give the new workers a moment.
            for i in 1 2 3 4 5 6 7 8 9 10; do
              page=$(c "$client" || true)
              echo "$page" | grep -q 'configuration/bootstrap.js' && break
              sleep 1
            done
            echo "$page" | grep -q 'configuration/bootstrap.js' || fail "$client does not serve Octane's dist/index.html."
            echo "  index.html OK"
            echo "> ${client}configuration/renderer-config.json"
            socket=$(c "${client}configuration/renderer-config.json" | grep -oE '"socket.url": *"[^"]*"' || true)
            [ -n "$socket" ] || fail "${client}configuration/renderer-config.json is not served."
            echo "  $socket"
            echo "> $url/gamedata/"
            echo "  HTTP $(c -o /dev/null -w '%{http_code}' "$url/gamedata/config/" || true) (403/404 until the assets are copied in)"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Publishing Octane failed", token);
    }
}
