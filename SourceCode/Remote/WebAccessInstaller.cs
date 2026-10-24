namespace AllInOneInstaller.Remote;

/// <summary>How the hotel is reached from the internet. With AtomCMS the CMS is the site and Octane moves to /client.</summary>
public sealed record WebAccessSettings(string Address, bool Https, bool Cloudflare, bool AtomCms = false)
{
    public string Scheme => Https ? "https" : "http";
    /// <summary>Where Octane is served: "/" or "/client/".</summary>
    public string ClientPath => AtomCms ? "/client/" : "/";
    public string ClientUrl => Url + ClientPath;
    public string Url => $"{Scheme}://{(Address.Contains(':') ? $"[{Address}]" : Address)}";
    /// <summary>Traefik then answers for every host name.</summary>
    public bool IsIpAddress => System.Net.IPAddress.TryParse(Address, out _);

    /// <summary>The settings in /opt/traefik/hotel.env, as printed with a conf_ prefix; null before the web access step.</summary>
    public static WebAccessSettings? FromHotelEnv(ScriptValues values) =>
        values["conf_ADDRESS"] is { } address
            ? new WebAccessSettings(address, values["conf_SCHEME"] == "https", values.Yes("conf_CLOUDFLARE"), values["conf_CMS"] == "atom")
            : null;
}

/// <summary>Docker, Traefik and NGINX state before the step runs.</summary>
public sealed record WebAccessState(
    string? DockerVersion,
    bool DockerFromDocker,
    IReadOnlyList<string> ConflictingPackages,
    bool DockerRunning,
    bool TraefikRunning,
    bool SiteEnabled,
    WebAccessSettings? Configured,
    string? Port80Process,
    string? Port443Process,
    bool EmulatorRunning)
{
    public bool IsComplete => DockerFromDocker && DockerRunning && TraefikRunning && SiteEnabled && Configured is not null;
}

public sealed record WebAccessResult(bool CertificateValid, string? CertificateProblem, bool ApiAnswered, bool WebSocketAnswered);

/// <summary>
/// Puts Traefik in Docker in front of the hotel on 80/443 with Let's Encrypt. /ws and /api go to Polaris,
/// everything else to NGINX on localhost:85.
/// </summary>
public static class WebAccessInstaller
{
    public const string TraefikDirectory = "/opt/traefik";
    public const string NginxPort = HotelSite.Port;
    public const string PolarisPort = "2096";
    public const string SiteFile = HotelSite.File;

    /// <summary>"Docker Release (CE deb)" key from download.docker.com/linux/&lt;distro&gt;/gpg.</summary>
    public const string DockerKeyFingerprint = "9DC858229FC7DD38854AE2D88D81803C0EBFCD88";

    // Every key currently in Docker's gpg file; any other key is refused.
    private static readonly string[] KnownDockerKeys = [DockerKeyFingerprint, "D3306A018370199E527AE7997EA0A9C3F273FCD8"];

    /// <summary>From docs.docker.com/engine/install, "Uninstall old versions".</summary>
    public const string ConflictingPackages = "docker.io docker-compose docker-compose-v2 docker-doc podman-docker containerd runc";

    // Go template braces cannot appear in the $$ raw strings.
    private const string GoRunning = "{{.State.Running}}";

    // Process name listening on the port, e.g. "nginx" or "docker-proxy"; empty when none.
    private static string PortOwner(int port) =>
        $"ss -Hltnp 'sport = :{port}' 2>/dev/null | grep -oE 'users:\\(\\(\"[^\"]+\"' | head -1 | cut -d'\"' -f2";

    public static async Task<WebAccessState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            echo "docker=$(dpkg-query -W -f='${db:Status-Abbrev}${Version}' docker-ce 2>/dev/null | awk '/^ii/{print substr($0,4)}')"
            echo "dockerorg=$(apt-cache policy docker-ce 2>/dev/null | grep -q download.docker.com && echo yes)"
            echo "conflicts=$(dpkg-query -W -f='${db:Status-Abbrev}${Package}\n' {{ConflictingPackages}} 2>/dev/null | awk '/^ii/{printf "%s ", substr($0,4)}')"
            echo "dockerrunning=$(docker info >/dev/null 2>&1 && echo yes)"
            echo "traefik=$(docker inspect -f '{{GoRunning}}' traefik 2>/dev/null)"
            echo "site=$([ -L /etc/nginx/sites-enabled/hotel ] && grep -qs 'listen 127.0.0.1:{{NginxPort}}' {{SiteFile}} && echo yes)"
            [ -f {{TraefikDirectory}}/hotel.env ] && sed 's/^/conf_/' {{TraefikDirectory}}/hotel.env
            echo "port80=$({{PortOwner(80)}})"
            echo "port443=$({{PortOwner(443)}})"
            echo "emulator=$(ss -Hltn 'sport = :{{PolarisPort}}' 2>/dev/null | grep -q . && echo yes)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);

        WebAccessSettings? configured = WebAccessSettings.FromHotelEnv(values);
        // "5:29.9.2-1~ubuntu.24.04~noble" -> "29.9.2"
        string? docker = values["docker"] is { } package ? package[(package.IndexOf(':') + 1)..].Split('-')[0] : null;
        return new WebAccessState(docker, values.Yes("dockerorg"),
            (values["conflicts"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            values.Yes("dockerrunning"), values["traefik"] == "true", values.Yes("site"), configured,
            values["port80"], values["port443"], values.Yes("emulator"));
    }

    /// <summary>Follows docs.docker.com/engine/install/ubuntu and /debian, with a verified key.</summary>
    public static Task InstallDockerAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string known = string.Join('|', KnownDockerKeys);
        string script = RemoteScript.Prelude + $$"""
            case "$ID" in
              ubuntu|debian) distro=$ID ;;
              *) fail "Docker's repository is for Ubuntu and Debian; this server runs $ID." ;;
            esac
            conflicts=$(dpkg-query -W -f='${db:Status-Abbrev}${Package}\n' {{ConflictingPackages}} 2>/dev/null | awk '/^ii/{printf "%s ", substr($0,4)}')
            if [ -n "$conflicts" ]; then
              echo "> Removing packages that conflict with Docker Engine: $conflicts"
              apt-get remove -y $conflicts
            fi

            echo "> Adding the Docker repository (download.docker.com/linux/$distro)"
            apt_update >/dev/null
            apt-get install -y ca-certificates curl gnupg
            install -m 0755 -d /etc/apt/keyrings
            tmp=$(mktemp)
            curl -fsSL "https://download.docker.com/linux/$distro/gpg" -o "$tmp" || fail "Could not download Docker's signing key."
            fingerprints=$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '$1=="fpr" {print $10}')
            echo "$fingerprints" | grep -qx '{{DockerKeyFingerprint}}' || { rm -f "$tmp"; fail "Docker's signing key does not have the published fingerprint {{DockerKeyFingerprint}}."; }
            for fpr in $fingerprints; do
              echo "$fpr" | grep -Eqx '{{known}}' || { rm -f "$tmp"; fail "Docker's key file contains an unknown key $fpr."; }
            done
            echo "  key {{DockerKeyFingerprint}} checked"
            install -m 0644 "$tmp" /etc/apt/keyrings/docker.asc
            rm -f "$tmp"
            # The format docs.docker.com uses now; an older docker.list would add the same repository twice.
            rm -f /etc/apt/sources.list.d/docker.list
            cat > /etc/apt/sources.list.d/docker.sources <<EOF
            Types: deb
            URIs: https://download.docker.com/linux/$distro
            Suites: $codename
            Components: stable
            Architectures: $(dpkg --print-architecture)
            Signed-By: /etc/apt/keyrings/docker.asc
            EOF
            apt_update 2>&1 | grep -E 'docker|^(E|W):' || true
            apt-cache policy docker-ce | grep -q download.docker.com || fail "Docker has no packages for $PRETTY_NAME ($codename) yet."

            echo "> apt-get install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin"
            apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

            echo "> Starting Docker"
            if [ -d /run/systemd/system ]; then
              systemctl enable --now docker
            elif ! docker info >/dev/null 2>&1; then
              service docker start || (setsid dockerd </dev/null >/var/log/dockerd.log 2>&1 &)
            fi
            for i in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
            docker info >/dev/null 2>&1 || fail "Docker is installed, but the Docker daemon does not run (see journalctl -u docker)."
            docker --version
            docker compose version
            """;
        return RemoteScript.RunAsync(connection, script, log, "Installing Docker failed", token);
    }

    /// <summary>Moves the hotel site to 127.0.0.1:85 and the default site off port 80, which Traefik needs.</summary>
    public static Task ConfigureNginxAsync(ServerConnection connection, WebAccessSettings settings, Action<string> log,
        CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + HotelSite.WriteScript(settings, root: null);
        return RemoteScript.RunAsync(connection, script, log, "Configuring NGINX failed", token);
    }

    /// <summary>With Cloudflare, X-Forwarded-For is trusted only from Cloudflare's published ranges.</summary>
    public static Task ConfigureTraefikAsync(ServerConnection connection, WebAccessSettings settings, Action<string> log,
        CancellationToken token = default)
    {
        string redirect = settings.Https ? """

                http:
                  redirections:
                    entryPoint:
                      to: websecure
                      scheme: https
            """ : "";
        string websecure = settings.Https ? """

              websecure:
                address: ":443"
                forwardedHeaders:
                  trustedIPs: [TRUSTED]
            """ : "";
        string acme = settings.Https ? """

            certificatesResolvers:
              letsencrypt:
                acme:
                  storage: /letsencrypt/acme.json
                  httpChallenge:
                    entryPoint: web
            """ : "";

        string script = RemoteScript.Prelude + $$"""
            t={{TraefikDirectory}}
            install -d -m 755 "$t" "$t/dynamic"
            install -d -m 700 "$t/letsencrypt"

            # Which proxies may set X-Forwarded-For: only Cloudflare (when used); otherwise Traefik writes the
            # visitor's address itself, so the header cannot be faked.
            trusted=
            if [ {{(settings.Cloudflare ? "yes" : "no")}} = yes ]; then
              load_cloudflare_ranges
              trusted=$(echo "$ranges" | sed 's/.*/"&"/' | paste -sd, -)
            fi

            echo "> $t/traefik.yml"
            cat > "$t/traefik.yml" <<'EOF'
            # Traefik static configuration (written by All-in-1 Installer).
            entryPoints:
              web:
                address: ":80"
                forwardedHeaders:
                  trustedIPs: [TRUSTED]{{redirect}}{{websecure}}
            providers:
              file:
                directory: /etc/traefik/dynamic
                watch: true{{acme}}
            log:
              level: INFO
              noColor: true
            EOF
            sed -i "s#\[TRUSTED\]#[$trusted]#" "$t/traefik.yml"

            {{RoutesScript(settings)}}

            # Host network: Traefik reaches NGINX and Polaris on 127.0.0.1, and Polaris sees 127.0.0.1 as the
            # (trusted) proxy that sets X-Forwarded-For.
            echo "> $t/compose.yaml"
            cat > "$t/compose.yaml" <<'EOF'
            # Traefik for the hotel (written by All-in-1 Installer). Update: docker compose pull && docker compose up -d
            services:
              traefik:
                image: traefik:v3
                container_name: traefik
                restart: unless-stopped
                network_mode: host
                volumes:
                  - ./traefik.yml:/etc/traefik/traefik.yml:ro
                  - ./dynamic:/etc/traefik/dynamic:ro
                  - ./letsencrypt:/letsencrypt
            EOF

            echo "> docker compose pull (traefik:v3)"
            pulled=
            for i in 1 2 3; do
              if docker compose -f "$t/compose.yaml" pull -q 2>"$t/pull.log"; then pulled=yes; break; fi
              tail -1 "$t/pull.log" | sed 's/^/  /'
              [ $i -lt 3 ] && { echo "  Trying again in $((i * 20)) seconds ..."; sleep $((i * 20)); }
            done
            rm -f "$t/pull.log"
            if [ -z "$pulled" ]; then
              docker image inspect traefik:v3 >/dev/null 2>&1 || fail "Could not download the Traefik image from Docker Hub (see above). Try again later."
              echo "  Docker Hub did not answer: using the traefik:v3 image that is already on this server"
            fi
            echo "> docker compose up -d"
            docker compose -f "$t/compose.yaml" up -d --force-recreate 2>&1 | grep -v '^ *$'
            for i in $(seq 1 20); do
              [ "$(docker inspect -f '{{GoRunning}}' traefik 2>/dev/null)" = true ] && ss -Hltn 'sport = :80' | grep -q . && break
              sleep 1
            done
            [ "$(docker inspect -f '{{GoRunning}}' traefik 2>/dev/null)" = true ] || { docker logs --tail 20 traefik 2>&1 | sed 's/^/  /'; fail "The Traefik container does not run."; }
            ss -Hltn 'sport = :80' | grep -q . || { docker logs --tail 20 traefik 2>&1 | sed 's/^/  /'; fail "Traefik runs, but does not listen on port 80."; }
            echo "  $(docker exec traefik traefik version 2>/dev/null | awk '/^Version/ {print "Traefik " $2}') is running"

            ufw_allow 80/tcp 443/tcp
            """;
        return RemoteScript.RunAsync(connection, script, log, "Setting up Traefik failed", token);
    }

    /// <summary>Polaris' own /api paths. With AtomCMS only these go to Polaris: the CMS has an /api of its own.</summary>
    public static readonly string[] PolarisApiPaths =
        ["auth", "authx", "badges", "cms", "emustats", "health", "housekeeping", "maintenance", "public", "wired"];

    /// <summary>Writes dynamic/hotel.yml and hotel.env in $t; Traefik picks up the routes without a restart.</summary>
    private static string RoutesScript(WebAccessSettings settings)
    {
        string entryPoint = settings.Https ? "websecure" : "web";
        string host = settings.IsIpAddress ? "" : $"Host(`{settings.Address}`) && ";
        string siteRule = settings.IsIpAddress ? "PathPrefix(`/`)" : $"Host(`{settings.Address}`)";
        string tls = settings.Https ? "\n      tls:\n        certResolver: letsencrypt" : "";
        string polaris = settings.AtomCms
            ? $"PathPrefix(`/ws`) || PathPrefix(`/nitro-sec`) || PathRegexp(`^/api/({string.Join('|', PolarisApiPaths)})(/|$)`)"
            : "PathPrefix(`/ws`) || PathPrefix(`/api`)";
        string description = settings.AtomCms ? "/ws and Polaris' /api paths to Polaris, the rest (AtomCMS, /client) to NGINX" : "/ws and /api to Polaris, the rest to NGINX";
        return $$"""
            echo "> $t/dynamic/hotel.yml: {{settings.Url}}{{description}}"
            cat > "$t/dynamic/hotel.yml" <<'EOF'
            # The hotel's routes (written by All-in-1 Installer).
            http:
              routers:
                polaris:
                  rule: "{{host}}({{polaris}})"
                  entryPoints: [{{entryPoint}}]
                  service: polaris
                  priority: 100{{tls}}
                hotel:
                  rule: "{{siteRule}}"
                  entryPoints: [{{entryPoint}}]
                  service: nginx
                  priority: 1{{tls}}
              services:
                polaris:
                  loadBalancer:
                    servers:
                      - url: "http://127.0.0.1:{{PolarisPort}}"
                nginx:
                  loadBalancer:
                    servers:
                      - url: "http://127.0.0.1:{{NginxPort}}"
            EOF

            cat > "$t/hotel.env" <<'EOF'
            ADDRESS={{settings.Address}}
            SCHEME={{settings.Scheme}}
            CLOUDFLARE={{(settings.Cloudflare ? "yes" : "no")}}
            CMS={{(settings.AtomCms ? "atom" : "none")}}
            EOF
            """;
    }

    /// <summary>Rewrites only the routes (for the AtomCMS addon); Traefik itself keeps running.</summary>
    public static Task ConfigureRoutesAsync(ServerConnection connection, WebAccessSettings settings, Action<string> log,
        CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            t={{TraefikDirectory}}
            [ -d "$t/dynamic" ] || fail "Traefik is not set up in $t. Run the Web access step first."
            {{RoutesScript(settings)}}
            """;
        return RemoteScript.RunAsync(connection, script, log, "Changing the Traefik routes failed", token);
    }

    /// <summary>
    /// Adds the hotel to ws.whitelist (websocket and CORS origin) and reads the visitor IP from X-Forwarded-For.
    /// Returns false when the emulator is not installed.
    /// </summary>
    public static async Task<bool> ConfigurePolarisAsync(ServerConnection connection, WebAccessSettings settings, Action<string> log,
        CancellationToken token = default)
    {
        string host = HardenInstaller.WhitelistHost(settings);
        string current = await EmulatorInstaller.ReadSettingAsync(connection, "ws.whitelist", token) ?? "";
        var hosts = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (current != "*" && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        string whitelist = current == "*" ? "*" : string.Join(',', hosts);
        return await EmulatorInstaller.SetSettingsAsync(connection,
        [
            ("ws.whitelist", whitelist),
            ("websockets.whitelist", whitelist),
            ("ws.ip.header", "X-Forwarded-For")
        ], "websocket whitelist and visitor IP", log, token: token);
    }

    /// <summary>
    /// Checks the site, /api/health and a websocket upgrade through local Traefik. With https, waits up to
    /// 2 minutes for the Let's Encrypt certificate.
    /// </summary>
    public static async Task<WebAccessResult> TestAsync(ServerConnection connection, WebAccessSettings settings, Action<string> log,
        CancellationToken token = default)
    {
        string port = settings.Https ? "443" : "80";
        string name = settings.IsIpAddress ? "hotel.invalid" : settings.Address;
        string origin = settings.Url;
        string script = RemoteScript.Prelude + $$"""
            url="{{settings.Scheme}}://{{name}}"
            # --noproxy: these requests are for this server itself, never for an HTTP proxy set in the environment.
            c() { curl -s --noproxy '*' --max-time 10 --resolve "{{name}}:{{port}}:127.0.0.1" --resolve "{{name}}:80:127.0.0.1" "$@"; }
            {{(settings.IsIpAddress ? "c() { curl -s --noproxy '*' --max-time 10 -k \"$@\"; }; url=\"" + settings.Scheme + "://127.0.0.1\"" : "")}}

            cert=no
            if [ "{{settings.Scheme}}" = https ]; then
              echo "> Waiting for the Let's Encrypt certificate for {{name}} (up to 2 minutes)"
              for i in $(seq 1 24); do
                if c -o /dev/null "$url/"; then cert=yes; echo "  certificate is valid after $((i * 5)) seconds"; break; fi
                if docker logs traefik 2>&1 | grep -qiE 'unable to obtain ACME certificate|acme: error'; then break; fi
                sleep 5
              done
              if [ $cert = no ]; then
                problem=$(docker logs traefik 2>&1 | grep -iE 'acme|certificate' | grep -iE 'error|unable' | tail -1 \
                  | sed 's/.*error="//; s/" [A-Za-z][A-Za-z ]*=.*//; s/\\"/"/g' | cut -c1-300)
                # (no apostrophe inside ${...:-...}: bash would take it as an open quote)
                [ -n "$problem" ] || problem="no answer from Let's Encrypt yet"
                echo "CERTPROBLEM=$problem"
                echo "  No valid certificate yet: $problem"
              fi
              echo "CERT=$cert"
              k=-k
            else
              k=
            fi

            echo "> $url/ (NGINX on port {{NginxPort}})"
            code=$(c $k -o /dev/null -w '%{http_code}' "$url/" || true)
            echo "  HTTP $code"
            # 302: AtomCMS sends a new install to its installer.
            case "$code" in 200|302|403) ;; *) fail "Traefik does not pass $url/ to NGINX (HTTP '$code'). See: docker logs traefik" ;; esac
            if [ "{{settings.Scheme}}" = https ]; then
              code=$(c -o /dev/null -w '%{http_code}' "http://{{name}}/" || true)
              case "$code" in
                301|302|307|308) echo "  http://{{name}}/ redirects to https:// (HTTP $code)" ;;
                *) echo "  http://{{name}}/ answers HTTP $code (expected a redirect to https://)" ;;
              esac
            fi

            echo "> $url/api/health (Polaris on port {{PolarisPort}})"
            body=$(c $k -w ' HTTP %{http_code}' "$url/api/health" || true)
            echo "  $body"
            echo "$body" | grep -q 'HTTP 200' && echo "API=yes"

            echo "> $url/ws (websocket upgrade, Origin {{origin}})"
            status=$(c $k --http1.1 -o /dev/null -w '%{http_code}' --max-time 5 \
              -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
              -H 'Sec-WebSocket-Key: SGVsbG8sIGhvdGVsIHRlc3Qh' -H 'Origin: {{origin}}' "$url/ws" || true)
            echo "  HTTP $status"
            [ "$status" = 101 ] && echo "WS=yes"
            true
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Testing the web access failed",
            ["CERT", "CERTPROBLEM", "API", "WS"], token);
        return new WebAccessResult(result.Yes("CERT"), result["CERTPROBLEM"], result.Yes("API"), result.Yes("WS"));
    }
}
