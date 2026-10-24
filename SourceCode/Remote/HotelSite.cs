namespace AllInOneInstaller.Remote;

/// <summary>The hotel's NGINX site on 127.0.0.1:85 behind Traefik, shared by the web access and Octane steps.</summary>
public static class HotelSite
{
    public const string File = "/etc/nginx/sites-available/hotel";
    public const string Port = "85";
    /// <summary>Written by Polaris, served by NGINX.</summary>
    public const string Camera = OctaneInstaller.Gamedata + "/camera";
    public const string LogDirectory = "/var/log/nginx/hotel";
    public const string DefaultRoot = "/usr/share/nginx/html";

    private const string Cors = """
                add_header Access-Control-Allow-Origin   $hotel_cors_origin always;
                add_header Access-Control-Allow-Methods  "GET, POST, OPTIONS" always;
                add_header Access-Control-Allow-Headers  "DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range" always;
                add_header Vary                          Origin always;
        """;

    private const string Preflight = """
                if ($request_method = OPTIONS) {
                    add_header Access-Control-Allow-Origin   $hotel_cors_origin always;
                    add_header Access-Control-Allow-Methods  "GET, POST, OPTIONS" always;
                    add_header Access-Control-Allow-Headers  "DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range" always;
                    add_header Access-Control-Max-Age        1728000 always;
                    add_header Vary                          Origin always;
                    add_header Content-Length                0;
                    return 204;
                }
        """;

    /// <summary>@ROOT@ is substituted on the server.</summary>
    public static string Config(WebAccessSettings site)
    {
        string serverName = site.IsIpAddress ? "_" : site.Address.StartsWith("www.", StringComparison.Ordinal)
            ? $"{site.Address} {site.Address[4..]}" : $"{site.Address} www.{site.Address}";
        string host = site.Address.StartsWith("www.", StringComparison.Ordinal) ? site.Address[4..] : site.Address;
        // /gamedata CORS: only the hotel's own pages, with or without www.
        string origin = site.IsIpAddress
            ? $"~^https?://{System.Text.RegularExpressions.Regex.Escape(site.Address)}$"
            : $"~^https?://(www\\.)?{System.Text.RegularExpressions.Regex.Escape(host)}$";
        string hsts = site.Https ? "    add_header Strict-Transport-Security \"max-age=2592000\" always;\n" : "";

        return $$"""
            # The hotel website (written by All-in-1 Installer; the web access and Octane steps rewrite it).
            # Traefik (/opt/traefik) answers on ports 80/443 for {{site.Url}} (and does the TLS) and passes the
            # site to this server on 127.0.0.1:{{Port}}; /ws and /api go straight to Polaris on 127.0.0.1:{{WebAccessInstaller.PolarisPort}}.

            map $http_origin $hotel_cors_origin {
                default "";
                "{{origin}}" $http_origin;
            }

            server {
                listen 127.0.0.1:{{Port}};
                server_name {{serverName}};

                root @ROOT@;
                index index.html index.php index.htm;

                autoindex     off;
                server_tokens off;

                # The visitor's address from Traefik, not 127.0.0.1.
                set_real_ip_from  127.0.0.1;
                real_ip_header    X-Forwarded-For;
                real_ip_recursive on;

                add_header X-Content-Type-Options "nosniff" always;
            {{hsts}}    add_header Referrer-Policy        "strict-origin";
                add_header X-XSS-Protection       "1; mode=block";
                add_header Permissions-Policy     "accelerometer=(), autoplay=(self), camera=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), sync-xhr=(), usb=()";

                client_header_buffer_size   1k;
                large_client_header_buffers 4 8k;

                gzip       on;
                gzip_types application/json application/javascript text/css image/svg+xml;

                access_log {{LogDirectory}}/access.log;
                error_log  {{LogDirectory}}/error.log;

                location / {
                    try_files $uri $uri/ /index.html;
                }

                location = /index.html {
                    add_header Cache-Control "no-cache" always;
                }

                location = /.vite/manifest.json {
                    try_files $uri =404;
                    default_type application/json;
                    add_header Cache-Control "no-store" always;
                }

                # Build output with a hash in the name never changes.
                location ~* ^/(assets|src/assets)/.+-[a-zA-Z0-9_]+\.(js|css|woff2?|ttf|otf|eot|svg|png|jpe?g|gif|webp|avif|map)$ {
                    try_files $uri =404;
                    expires 1y;
                    add_header Cache-Control "public, immutable" always;
                }

                # Octane's runtime configuration: always the current version.
                location /configuration/ {
                    try_files $uri =404;
                    add_header Cache-Control "no-store" always;
                }

                # The hotel's assets (copied in with the All-in-1 download tool) and the camera photos.
                location /gamedata/ {
                    alias {{OctaneInstaller.Gamedata}}/;

            {{Preflight}}
            {{Cors}}

                    try_files $uri $uri/ =404;

                    # A nested location does not inherit the preflight above: repeat it.
                    location ~* \.(jpg|jpeg|png|gif|ico|nitro|hab)$ {
                        if ($request_method = OPTIONS) {
                            add_header Access-Control-Allow-Origin   $hotel_cors_origin always;
                            add_header Access-Control-Allow-Methods  "GET, POST, OPTIONS" always;
                            add_header Access-Control-Allow-Headers  "DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range" always;
                            add_header Access-Control-Max-Age        1728000 always;
                            add_header Vary                          Origin always;
                            add_header Content-Length                0;
                            return 204;
                        }
                        expires 30d;
                        add_header Cache-Control               "public, no-transform";
                        add_header Access-Control-Allow-Origin $hotel_cors_origin always;
                        add_header Vary                        Origin            always;
                    }
                }

                location /custom/ {
                    alias {{OctaneInstaller.Gamedata}}/custom/;
                }
            }
            """;
    }

    /// <summary>
    /// Also disables other sites on 80/443 and checks 127.0.0.1:85. A null <paramref name="root"/> keeps the
    /// current root.
    /// </summary>
    public static string WriteScript(WebAccessSettings site, string? root) => $$"""
        [ -d /etc/nginx/sites-available ] || fail "NGINX is not installed with sites-available. Run the NGINX step first."
        if [ -f {{File}} ] && ! grep -q 'All-in-1 Installer' {{File}}; then
          cp {{File}} "{{File}}.bak-$(date +%Y%m%d-%H%M%S)"
          echo "  The old {{File}} is saved as {{File}}.bak-<time>"
        fi
        root='{{root ?? ""}}'
        [ -n "$root" ] || { [ -f {{File}} ] && root=$(awk '$1=="root" {gsub(/;/, "", $2); print $2; exit}' {{File}}); }
        [ -n "$root" ] && [ "$root" != "@ROOT@" ] || root={{DefaultRoot}}

        install -d -m 755 {{LogDirectory}}
        install -d -m 2775 -o www-data -g www-data {{OctaneInstaller.Gamedata}} {{OctaneInstaller.Gamedata}}/custom \
          {{Camera}} {{Camera}}/thumb
        # NGINX (user nginx) reads what the emulator writes there (group www-data, mode 640).
        if id nginx >/dev/null 2>&1 && ! id -nG nginx | grep -qw www-data; then
          usermod -aG www-data nginx
          echo "  nginx is now in the www-data group (to read the emulator's camera photos)"
        fi

        echo "> {{File}}: listen 127.0.0.1:{{Port}}, root $root"
        cat > {{File}} <<'SITE'
        {{Config(site)}}
        SITE
        sed -i "s#@ROOT@#$root#" {{File}}
        ln -sfn {{File}} /etc/nginx/sites-enabled/hotel

        # Port 80 is Traefik's: the default site (and any other site on 80/443) must not take it.
        if [ -L /etc/nginx/sites-enabled/default ] || [ -f /etc/nginx/sites-enabled/default ]; then
          rm -f /etc/nginx/sites-enabled/default
          echo "  The default site is disabled (still in sites-available/default)"
        fi
        for f in /etc/nginx/conf.d/*.conf /etc/nginx/sites-enabled/*; do
          [ -f "$f" ] || continue
          if grep -Eq '^[[:space:]]*listen[[:space:]]+(\[::\]:|0\.0\.0\.0:)?(80|443)([[:space:]]|;)' "$f"; then
            fail "$f listens on port 80 or 443, which Traefik needs. Move that site to 127.0.0.1:{{Port}} (or disable it) and try again."
          fi
        done

        nginx -t 2>&1 | sed 's/^/  /'
        nginx -t >/dev/null 2>&1 || fail "nginx -t refuses the configuration (see above)."
        if [ -d /run/systemd/system ]; then systemctl reload nginx || systemctl restart nginx; else nginx -s reload 2>/dev/null || service nginx restart; fi
        sleep 1
        code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' http://127.0.0.1:{{Port}}/ || true)
        echo "  http://127.0.0.1:{{Port}}/ answers $code"
        [ "$code" = 200 ] || [ "$code" = 403 ] || fail "NGINX does not answer on 127.0.0.1:{{Port}} (HTTP '$code')."
        """;
}
