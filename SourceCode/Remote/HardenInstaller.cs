namespace AllInOneInstaller.Remote;

/// <summary>Hardening state before the step runs.</summary>
public sealed record HardenState(
    WebAccessSettings? Site,
    bool UfwActive,
    bool DenyIncoming,
    IReadOnlyList<int> SshPorts,
    bool MariaDbOpen,
    bool SysctlConfigured,
    string? Whitelist,
    string? IpHeader)
{
    /// <summary>Polaris strips "www." itself, so the bare address is enough.</summary>
    public string? ExpectedWhitelist => Site is null ? null : HardenInstaller.WhitelistHost(Site);

    public bool IsComplete => Site is not null && UfwActive && DenyIncoming && !MariaDbOpen && SysctlConfigured &&
                              Whitelist == ExpectedWhitelist && IpHeader == "X-Forwarded-For";
}

/// <summary>ufw firewall, Polaris origin and IP header settings, and SYN flood protection.</summary>
public static class HardenInstaller
{
    /// <summary>
    /// Once present, other steps stop opening ports in ufw. ufw_allow in RemoteScript.Prelude checks the same path.
    /// </summary>
    public const string Marker = "/etc/ufw/all-in-1-hardened";

    public static readonly (string Key, string Value)[] Sysctl =
    [
        ("net.ipv4.tcp_syncookies", "1"),
        ("net.ipv4.tcp_max_syn_backlog", "2048"),
        ("net.ipv4.tcp_synack_retries", "3")
    ];

    public static string WhitelistHost(WebAccessSettings site) =>
        site.Address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? site.Address[4..] : site.Address;

    public static async Task<HardenState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string sysctlCheck = string.Join(" && ", Sysctl.Select(s => $"grep -Eq '^[[:space:]]*{s.Key}[[:space:]]*=[[:space:]]*{s.Value}[[:space:]]*$' /etc/sysctl.conf"));
        string script = $$"""
            [ -f {{WebAccessInstaller.TraefikDirectory}}/hotel.env ] && sed 's/^/conf_/' {{WebAccessInstaller.TraefikDirectory}}/hotel.env
            status=$(ufw status verbose 2>/dev/null)
            echo "active=$(echo "$status" | grep -q '^Status: active' && echo yes)"
            echo "deny=$(echo "$status" | grep -q 'deny (incoming)' && echo yes)"
            # sshd's listeners plus its configured ports. On Ubuntu 24.04+ systemd holds the socket through ssh.socket.
            echo "ssh=$( (ss -Hltnp 2>/dev/null | grep -E '"sshd"|ssh.socket' | awk '{print $4}' | sed 's/.*://'; sshd -T 2>/dev/null | awk '$1=="port" {print $2}') | sort -un | tr '\n' ' ')"
            echo "mariadb=$(echo "$status" | grep -qE '^3306[/ ].*ALLOW' && echo open)"
            echo "sysctl=$({{sysctlCheck}} && echo yes)"
            db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2)
            if [ -n "$db" ]; then
              echo "whitelist=$(mariadb -N "$db" -e "SELECT \`value\` FROM emulator_settings WHERE \`key\` = 'ws.whitelist'" 2>/dev/null)"
              echo "ipheader=$(mariadb -N "$db" -e "SELECT \`value\` FROM emulator_settings WHERE \`key\` = 'ws.ip.header'" 2>/dev/null)"
            fi
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);

        WebAccessSettings? site = WebAccessSettings.FromHotelEnv(values);
        var ports = (values["ssh"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out int n) ? n : 0).Where(n => n > 0).ToList();
        return new HardenState(site, values.Yes("active"), values.Yes("deny"), ports,
            values["mariadb"] == "open", values.Yes("sysctl"), values["whitelist"], values["ipheader"]);
    }

    /// <summary>
    /// Resets ufw, which backs up the old rules in /etc/ufw. Rate-limits SSH on every sshd port plus this session's,
    /// and opens HTTP/HTTPS to everyone or only Cloudflare. MariaDB and the emulator ports stay closed.
    /// </summary>
    public static Task ConfigureFirewallAsync(ServerConnection connection, WebAccessSettings site, IReadOnlyList<int> sshPorts,
        Action<string> log, CancellationToken token = default)
    {
        string ports = string.Join(' ', sshPorts.Append(connection.Login.Port).Distinct().Order());
        string script = RemoteScript.Prelude + $$"""
            if ! command -v ufw >/dev/null 2>&1; then
              echo "> Installing ufw"
              apt_update >/dev/null
              apt-get install -y ufw 2>&1 | grep -v 'Reading database' || true
              command -v ufw >/dev/null 2>&1 || fail "ufw could not be installed (see above)."
            fi
            # ufw only takes IPv6 rules when IPv6 is on in /etc/default/ufw and on the server.
            ipv6=yes
            grep -qi '^IPV6=yes' /etc/default/ufw 2>/dev/null && [ -f /proc/net/if_inet6 ] || ipv6=

            # Download Cloudflare's ranges before anything changes, so a failed download changes nothing.
            ranges=
            if [ {{(site.Cloudflare ? "yes" : "no")}} = yes ]; then
              load_cloudflare_ranges "the firewall was not changed"
              [ -n "$ipv6" ] || { ranges=$(echo "$ranges" | grep -v ':'); echo "  IPv6 is off: only the IPv4 ranges are used"; }
            fi

            echo "> ufw: starting from a clean rule set (ufw keeps the old rules as /etc/ufw/*.rules.<date>)"
            ufw --force reset >/dev/null
            ufw default deny incoming
            ufw default allow outgoing
            for port in {{ports}}; do
              ufw limit "$port/tcp" comment 'Rate limit for openssh server'
            done

            if [ -n "$ranges" ]; then
              echo "> HTTP/HTTPS only from Cloudflare"
              for range in $ranges; do
                ufw allow proto tcp from "$range" to any port 80,443 comment 'Cloudflare' >/dev/null
              done
              echo "  $(echo "$ranges" | grep -c .) Cloudflare ranges may reach ports 80 and 443"
            else
              ufw allow 80/tcp comment 'HTTP (Traefik)'
              ufw allow 443/tcp comment 'HTTPS (Traefik)'
            fi

            echo "  MariaDB (3306) and the emulator's ports stay closed (use an SSH tunnel for HeidiSQL)"

            echo "> ufw enable"
            ufw --force enable
            printf 'Firewall set up by the All-in-1 Installer harden step on %s.\nThe other steps no longer open ports; run the harden step again to change the rules.\n' "$(date)" > {{Marker}}
            ufw status verbose | sed 's/^/  /' | grep -v '^  $' | grep -v 'Cloudflare' || true
            [ -z "$ranges" ] || echo "  (+ $(ufw status | grep -c Cloudflare) Cloudflare rules for 80,443)"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Setting up the firewall failed", token);
    }

    /// <summary>
    /// Tools → Enable firewall. If the harden step's rules exist they are switched on again, after making sure every
    /// SSH port including this session's is still allowed. Otherwise the harden step's rule set is written.
    /// Returns true when the full rule set was written.
    /// </summary>
    public static async Task<bool> EnableFirewallAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        HardenState state = await InspectAsync(connection, token);
        string check = $"""
            command -v ufw >/dev/null 2>&1 && echo "ufw=yes"
            [ -f {Marker} ] && echo "marker=yes"
            echo "rules=$(ufw show added 2>/dev/null | grep -c '^ufw ')"
            """;
        var values = new ScriptValues((await connection.RunAsRootAsync(check, token)).Output);
        if (!values.Yes("ufw") || !values.Yes("marker") || values.Int("rules") == 0)
        {
            log("> No firewall rules from the harden step yet: setting them up (the same rules as the Harden step)");
            WebAccessSettings site = state.Site ?? new WebAccessSettings("", false, false);
            await ConfigureFirewallAsync(connection, site, state.SshPorts, log, token);
            return true;
        }

        string ports = string.Join(' ', state.SshPorts.Append(connection.Login.Port).Distinct().Order());
        string script = RemoteScript.Prelude + $$"""
            # Never lock out SSH. Every sshd port and this session's must be in the rules before ufw is switched on.
            for port in {{ports}}; do
              if ! ufw show added | grep -Eq "^ufw (limit|allow) ($port|$port/tcp)( |$)"; then
                echo "> SSH port $port was not in the rules: adding it (rate limited)"
                ufw limit "$port/tcp" comment 'Rate limit for openssh server'
              fi
            done
            echo "> ufw enable (the rules of the harden step)"
            ufw --force enable
            ufw status verbose | sed 's/^/  /' | grep -v '^  $' | grep -v 'Cloudflare' || true
            n=$(ufw status | grep -c Cloudflare); [ "$n" -eq 0 ] || echo "  (+ $n Cloudflare rules for 80,443)"
            """;
        await RemoteScript.RunAsync(connection, script, log, "Switching on the firewall failed", token);
        return false;
    }

    /// <summary>Tools → Disable firewall. The rules are kept, so Enable firewall switches the same rules on again.</summary>
    public static Task DisableFirewallAsync(ServerConnection connection, Action<string> log, CancellationToken token = default) =>
        RemoteScript.RunAsync(connection, RemoteScript.Prelude + """
            if ! command -v ufw >/dev/null 2>&1; then
              echo "  ufw is not installed: there is no firewall to switch off"
              exit 0
            fi
            echo "> ufw disable"
            ufw disable
            echo "  $(ufw show added 2>/dev/null | grep -c '^ufw ') rules are kept for Enable firewall"
            ufw status | sed 's/^/  /'
            """, log, "Switching off the firewall failed", token);

    /// <summary>Replaces existing lines for these keys in /etc/sysctl.conf and applies them now.</summary>
    public static Task ConfigureSysctlAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string lines = string.Join('\n', Sysctl.Select(s => $"""
            if grep -Eq '^[[:space:]]*#?[[:space:]]*{s.Key}[[:space:]]*=' /etc/sysctl.conf; then
              sed -i -E 's|^[[:space:]]*#?[[:space:]]*{s.Key}[[:space:]]*=.*|{s.Key}={s.Value}|' /etc/sysctl.conf
            else
              [ -n "$added" ] || printf '\n# SYN flood protection (All-in-1 Installer)\n' >> /etc/sysctl.conf
              added=yes
              echo '{s.Key}={s.Value}' >> /etc/sysctl.conf
            fi
            if sysctl -w '{s.Key}={s.Value}' >/dev/null 2>&1; then echo "  {s.Key} = $(sysctl -n {s.Key})"; else echo "  {s.Key}={s.Value} is in /etc/sysctl.conf (this machine does not allow changing it now)"; fi
            """));
        string script = RemoteScript.Prelude + $$"""
            echo "> /etc/sysctl.conf"
            touch /etc/sysctl.conf
            cp /etc/sysctl.conf "/etc/sysctl.conf.bak-$(date +%Y%m%d-%H%M%S)"
            added=
            {{lines}}
            """;
        return RemoteScript.RunAsync(connection, script, log, "Changing the TCP settings failed", token);
    }

    /// <summary>Restricts ws.whitelist to the hotel and trusts X-Forwarded-For, which Traefik sets.</summary>
    public static Task<bool> ConfigurePolarisAsync(ServerConnection connection, WebAccessSettings site, Action<string> log,
        CancellationToken token = default)
    {
        string host = WhitelistHost(site);
        return EmulatorInstaller.SetSettingsAsync(connection,
        [
            ("ws.whitelist", host),
            ("websockets.whitelist", host),
            ("ws.ip.header", "X-Forwarded-For")
        ], "websocket whitelist and visitor IP", log, token: token);
    }
}
