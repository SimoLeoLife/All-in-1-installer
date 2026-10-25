namespace AllInOneInstaller.Remote;

/// <summary>sshd's effective login settings (sshd -T) and whether a saved key is in root's authorized_keys.</summary>
public sealed record SshLoginState(bool PasswordLogin, bool KeyboardInteractive, string? PermitRootLogin, bool OurFile, bool RootHasSavedKey)
{
    public bool KeysOnly => !PasswordLogin && !KeyboardInteractive;
}

/// <summary>Turns SSH password login off or back on with a drop-in file that sshd reads before the rest.</summary>
public static class SshLoginInstaller
{
    /// <summary>sshd keeps the first value it reads and reads sshd_config.d in name order, so 00- wins.</summary>
    public const string DropIn = "/etc/ssh/sshd_config.d/00-all-in-1-keys-only.conf";

    // A reload is enough for new logins. The restart fallback also keeps open sessions, this one included.
    private const string Reload = """
        reload_sshd() {
          if [ -d /run/systemd/system ]; then
            systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || systemctl restart ssh 2>/dev/null || systemctl restart sshd
          elif [ -f /run/sshd.pid ]; then
            kill -HUP "$(cat /run/sshd.pid)"
          else
            service ssh reload 2>/dev/null || service sshd reload
          fi
          sleep 1
        }
        """;

    public static async Task<SshLoginState> InspectAsync(ServerConnection connection, IReadOnlyList<AuthorizedKey> savedKeys,
        CancellationToken token = default)
    {
        string script = $$"""
            sshd -T 2>/dev/null | awk '$1 == "passwordauthentication" || $1 == "kbdinteractiveauthentication" || $1 == "permitrootlogin" { print $1 "=" $2 }'
            echo "ours=$([ -f {{DropIn}} ] && echo yes)"
            echo "keys=$(awk '{ for (i = 1; i < NF; i++) if ($i ~ /^(ssh-|ecdsa-|sk-)/) { print $(i + 1); break } }' /root/.ssh/authorized_keys 2>/dev/null | paste -sd' ' -)"
            """;
        var values = new ScriptValues((await connection.RunAsRootAsync(script, token)).Output);
        var rootKeys = (values["keys"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return new SshLoginState(
            values["passwordauthentication"] != "no",
            values["kbdinteractiveauthentication"] != "no",
            values["permitrootlogin"],
            values.Yes("ours"),
            savedKeys.Any(k => rootKeys.Contains(k.Base64)));
    }

    /// <summary>Writes the keys-only drop-in, validates and reloads sshd, and puts everything back when a check fails.</summary>
    public static Task DisablePasswordLoginAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Reload}}
            c=/etc/ssh/sshd_config
            [ -f "$c" ] || fail "$c is missing: is OpenSSH server installed?"
            backup="$c.bak-$(date +%Y%m%d-%H%M%S)"
            cp "$c" "$backup"
            undo() {
              rm -f {{DropIn}}
              cp "$backup" "$c"
              sshd -t 2>/dev/null && reload_sshd
              echo "  Undone: password login works as before."
            }
            install -d -m 755 /etc/ssh/sshd_config.d
            if ! grep -Eq '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config.d/\*\.conf' "$c"; then
              echo "> $c: adding Include /etc/ssh/sshd_config.d/*.conf at the top"
              sed -i '1i Include /etc/ssh/sshd_config.d/*.conf' "$c"
            fi
            echo "> {{DropIn}}"
            cat > {{DropIn}} <<'CONF'
            # Written by All-in-1 Installer (SSH login step): only SSH keys can log in.
            # Lost your key? Log in on the console of your hosting provider (KVM / VNC / rescue) and delete this file,
            # then: systemctl reload ssh
            PasswordAuthentication no
            KbdInteractiveAuthentication no
            PermitRootLogin prohibit-password
            PubkeyAuthentication yes
            CONF
            chmod 644 {{DropIn}}
            if ! out=$(sshd -t 2>&1); then
              echo "$out" | sed 's/^/  /'
              undo
              fail "sshd -t refuses the new settings (see above); nothing was changed."
            fi
            echo "> Reloading sshd (open sessions stay connected)"
            reload_sshd
            effective=$(sshd -T 2>/dev/null | awk '$1 == "passwordauthentication" || $1 == "kbdinteractiveauthentication" || $1 == "permitrootlogin" { print "  " $1 " " $2 }')
            echo "$effective"
            if echo "$effective" | grep -Eq '(passwordauthentication|kbdinteractiveauthentication) yes'; then
              undo
              fail "sshd still allows passwords: another setting comes first. Nothing was changed."
            fi
            """;
        return RemoteScript.RunAsync(connection, script, log, "Turning password login off failed", token);
    }

    /// <summary>Removes the drop-in so password login follows the rest of the sshd configuration again.</summary>
    public static Task AllowPasswordLoginAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            {{Reload}}
            if [ -f {{DropIn}} ]; then
              echo "> Removing {{DropIn}}"
              rm -f {{DropIn}}
              sshd -t 2>&1 | sed 's/^/  /'
              reload_sshd
            else
              echo "  {{DropIn}} is not there: nothing to undo."
            fi
            sshd -T 2>/dev/null | awk '$1 == "passwordauthentication" || $1 == "kbdinteractiveauthentication" || $1 == "permitrootlogin" { print "  " $1 " " $2 }'
            """;
        return RemoteScript.RunAsync(connection, script, log, "Allowing password login failed", token);
    }
}
