namespace AllInOneInstaller.Remote;

/// <summary>Runs install scripts as root with live output.</summary>
public static class RemoteScript
{
    // Prepended to every install script.
    public const string Prelude = """
        set -e
        export DEBIAN_FRONTEND=noninteractive
        . /etc/os-release
        codename="${VERSION_CODENAME:-$(lsb_release -cs 2>/dev/null)}"
        fail() { echo "ERROR: $*"; exit 1; }

        # apt refuses repository files dated in the future ("Release file ... is not valid yet"), so a server
        # clock that runs behind breaks every apt-get update. Compare it with the Date header of a trusted
        # HTTPS server and correct it once, before the first update.
        clock_checked=
        fix_clock() {
          [ -n "$clock_checked" ] && return 0
          clock_checked=1
          remote=""
          for url in https://deb.debian.org/ https://archive.ubuntu.com/; do
            if command -v curl >/dev/null 2>&1; then
              remote=$(curl -fsSI --max-time 10 "$url" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:" {sub(/^[^:]*: */, ""); print; exit}')
            elif command -v wget >/dev/null 2>&1; then
              remote=$(wget -qS --spider -T 10 "$url" 2>&1 | tr -d '\r' | awk 'tolower($1)=="date:" {sub(/^[^:]*: */, ""); print; exit}')
            fi
            [ -n "$remote" ] && break
          done
          [ -n "$remote" ] || { echo "  (could not check the server clock: no curl/wget or no internet)"; return 0; }
          real=$(date -d "$remote" +%s 2>/dev/null) || return 0
          skew=$((real - $(date +%s)))
          if [ "${skew#-}" -gt 120 ]; then
            echo "  The server clock is off by $skew seconds: setting it to $remote"
            date -u -s "@$real" >/dev/null || fail "The server clock is off by $skew seconds and could not be corrected. Set the right time, then try again."
            hwclock --systohc >/dev/null 2>&1 || true
            if command -v timedatectl >/dev/null 2>&1 && [ -d /run/systemd/system ] && timedatectl set-ntp true >/dev/null 2>&1; then
              echo "  Automatic time sync (NTP) is on"
            else
              echo "  Automatic time sync could not be switched on: install systemd-timesyncd or chrony to keep the time right"
            fi
          fi
        }
        apt_update() { fix_clock; apt-get update; }

        # download URL FILE, with timeouts and retries and a last attempt over IPv4. Tools that download by
        # themselves (composer-setup.php, mvnw) have no timeout and hang forever on a broken IPv6 route.
        download() {
          curl -fsSL --connect-timeout 20 --max-time 900 --retry 2 --retry-delay 5 -o "$2" "$1" && return 0
          echo "  Trying again over IPv4 ..."
          curl -fsSL -4 --connect-timeout 20 --max-time 900 --retry 2 --retry-delay 5 -o "$2" "$1"
        }

        # Opens ports when ufw is active, unless the harden step manages the firewall.
        ufw_allow() {
          if [ -f /etc/ufw/all-in-1-hardened ]; then
            echo "  The firewall is managed by the Harden step: no port opened (run that step again to change it)"
            return 0
          fi
          command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active' || return 0
          echo "> ufw is active: allowing $*"
          for port in "$@"; do ufw allow "$port"; done
        }

        # Sets $ranges to Cloudflare's published IP ranges, one per line. $1 is added to the error message.
        load_cloudflare_ranges() {
          echo "> Downloading Cloudflare's IP ranges (www.cloudflare.com/ips-v4, ips-v6)"
          ranges=$( (curl -fsS --max-time 20 https://www.cloudflare.com/ips-v4; echo; curl -fsS --max-time 20 https://www.cloudflare.com/ips-v6) \
            | grep -E '^[0-9a-f.:]+/[0-9]+$' ) || true
          [ "$(echo "$ranges" | grep -c .)" -ge 10 ] || fail "Could not download Cloudflare's IP ranges${1:+; $1}."
          echo "  $(echo "$ranges" | grep -c .) ranges"
        }
        """ + "\n"; // so whatever is appended starts on its own line

    /// <summary>On a non-zero exit, throws with the last fail() message, or else the last output line.</summary>
    public static async Task RunAsync(ServerConnection connection, string script, Action<string> log, string failure,
        CancellationToken token = default, string? stdin = null)
    {
        string? error = null, last = null;
        int exitCode = await connection.RunAsRootStreamingAsync(script, line =>
        {
            if (line.StartsWith("ERROR: ", StringComparison.Ordinal)) error = line["ERROR: ".Length..];
            if (line.Trim().Length > 0) last = line.Trim();
            log("  " + line);
        }, token, stdin);
        if (exitCode != 0)
            throw new ServerConnectionException($"{failure}: {error ?? last ?? $"exit code {exitCode}"}");
    }

    /// <summary>Like <see cref="RunAsync"/>, but returns the "KEY=value" lines for <paramref name="keys"/> instead of logging them.</summary>
    public static async Task<ScriptValues> RunCapturingAsync(ServerConnection connection, string script, Action<string> log,
        string failure, string[] keys, CancellationToken token = default, string? stdin = null)
    {
        var captured = new List<string>();
        await RunAsync(connection, script, line =>
        {
            string text = line.Trim();
            if (keys.Any(key => text.StartsWith(key + "=", StringComparison.Ordinal))) captured.Add(text);
            else log(line);
        }, failure, token, stdin);
        return new ScriptValues(captured);
    }
}
