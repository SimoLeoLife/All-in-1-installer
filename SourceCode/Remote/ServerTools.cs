namespace AllInOneInstaller.Remote;

/// <summary>A database dump on the server.</summary>
public sealed record DatabaseBackup(string RemotePath, string Database, long Bytes);

public sealed record EmulatorUpdate(string? OldVersion, string? NewVersion, bool Changed);

/// <summary>Restarts, database backup and import, and updates for the Tools page.</summary>
public static class ServerTools
{
    /// <summary>Database dumps made by the tools. Only root can read them because they hold every password hash.</summary>
    public const string BackupDirectory = "/var/backups/all-in-1";
    public const int KeptBackups = 10;

    // The hotel database from the emulator's config.ini, or habbo.
    private const string Database = $$"""
        db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2 | tr -d '[:space:]')
        db=${db:-habbo}
        """;

    private const string Systemd = "[ -d /run/systemd/system ]";

    public static Task RestartEmulatorAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            d={{EmulatorInstaller.Directory}}
            log={{EmulatorInstaller.LogDirectory}}/emulator.log
            [ -x "$d/emulator" ] || fail "The emulator is not set up on this server (no $d/emulator). Run the Emulator step first."
            {{EmulatorInstaller.ControlFunctions}}
            echo "> Restarting the emulator"
            start_emulator
            wait_loaded
            grep -E 'Started (Game|RCON) Server' "$log" | sed 's/^.*| /  /' || true
            """;
        return RemoteScript.RunAsync(connection, script, log, "Restarting the emulator failed", token);
    }

    public static Task RestartMariaDbAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            command -v mariadb >/dev/null || fail "MariaDB is not installed on this server."
            echo "> Restarting MariaDB"
            if {{Systemd}}; then systemctl restart mariadb; else service mariadb restart; fi
            for i in $(seq 1 30); do mariadb -N -e 'SELECT 1' >/dev/null 2>&1 && break; sleep 1; done
            mariadb -N -e 'SELECT 1' >/dev/null 2>&1 || fail "MariaDB does not answer after the restart (journalctl -u mariadb)."
            echo "  MariaDB $(mariadb -N -e 'SELECT VERSION()') is running"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Restarting MariaDB failed", token);
    }

    /// <summary>Restarts NGINX, and PHP-FPM too when it is installed because AtomCMS runs on it.</summary>
    public static Task RestartNginxAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            command -v nginx >/dev/null || fail "NGINX is not installed on this server."
            echo "> nginx -t"
            nginx -t 2>&1 | sed 's/^/  /'
            nginx -t >/dev/null 2>&1 || fail "nginx -t refuses the configuration (see above): NGINX was not restarted."
            if command -v php-fpm8.5 >/dev/null 2>&1; then
              echo "> Restarting PHP-FPM 8.5"
              if {{Systemd}}; then systemctl restart php8.5-fpm; else service php8.5-fpm restart; fi
            fi
            echo "> Restarting NGINX"
            if {{Systemd}}; then systemctl restart nginx; else nginx -s stop 2>/dev/null; sleep 1; nginx; fi
            sleep 1
            code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' http://127.0.0.1:{{HotelSite.Port}}/ || true)
            echo "  http://127.0.0.1:{{HotelSite.Port}}/ answers HTTP $code"
            case "$code" in 200|302|403) ;; *) fail "NGINX does not answer on 127.0.0.1:{{HotelSite.Port}} after the restart." ;; esac
            """;
        return RemoteScript.RunAsync(connection, script, log, "Restarting NGINX failed", token);
    }

    public static Task RestartTraefikAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            docker inspect traefik >/dev/null 2>&1 || fail "There is no Traefik container on this server. Run the Web access step first."
            echo "> docker restart traefik"
            docker restart traefik >/dev/null
            for i in $(seq 1 20); do ss -Hltn 'sport = :80' | grep -q . && break; sleep 1; done
            ss -Hltn 'sport = :80' | grep -q . || { docker logs --tail 20 traefik 2>&1 | sed 's/^/  /'; fail "Traefik does not listen on port 80 after the restart."; }
            echo "  $(docker exec traefik traefik version 2>/dev/null | awk '/^Version/ {print "Traefik " $2}') is running"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Restarting Traefik failed", token);
    }

    /// <summary>
    /// Dumps the hotel database with CREATE DATABASE and USE to a .sql.gz and keeps the newest <see cref="KeptBackups"/>.
    /// </summary>
    public static async Task<DatabaseBackup> BackupDatabaseAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            set -o pipefail
            {{Database}}
            mariadb -N "$db" -e 'SELECT 1' >/dev/null 2>&1 || fail "Database $db does not exist or MariaDB does not run."
            install -d -m 700 {{BackupDirectory}}
            file={{BackupDirectory}}/$db-$(date +%Y%m%d-%H%M%S).sql.gz
            echo "> mariadb-dump $db ($(mariadb -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$db'") tables) > $file"
            umask 077
            start=$(date +%s)
            mariadb-dump --single-transaction --quick --routines --triggers --events --hex-blob --databases "$db" | gzip -6 > "$file" ||
              { rm -f "$file"; fail "mariadb-dump stopped (see above)."; }
            gzip -t "$file" || fail "The backup $file is damaged."
            echo "  $(du -h "$file" | cut -f1) in $(( $(date +%s) - start )) seconds"
            # Keep the newest backups of this database, not counting the safety backups made before an import.
            ls -1t {{BackupDirectory}}/"$db"-2*.sql.gz 2>/dev/null | tail -n +{{KeptBackups + 1}} | while read -r old; do
              rm -f "$old"; echo "  Removed the old backup $old"
            done
            echo "FILE=$file"
            echo "DB=$db"
            echo "BYTES=$(stat -c %s "$file")"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "The database backup failed",
            ["FILE", "DB", "BYTES"], token);
        return new DatabaseBackup(result["FILE"] ?? "", result["DB"] ?? "", long.TryParse(result["BYTES"], out long bytes) ? bytes : 0);
    }

    /// <summary>
    /// Replaces the hotel database with a .sql or .sql.gz dump after a safety backup. The database keeps its character
    /// set, and names in CREATE DATABASE and USE lines become the hotel's. The emulator runs its migrations on restart.
    /// </summary>
    public static Task ImportDatabaseAsync(ServerConnection connection, string remoteFile, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            set -o pipefail
            {{Database}}
            f={{ServerConnection.ShellQuote(remoteFile)}}
            [ -s "$f" ] || fail "The uploaded file $f is missing or empty."
            # The upload is only needed for this import, whatever happens.
            chmod 600 "$f"
            trap 'rm -f "$f"' EXIT
            if gzip -t "$f" 2>/dev/null; then read_dump() { gunzip -c "$f"; }; else read_dump() { cat "$f"; }; fi
            # No pipefail here because head ends the pipe early on purpose.
            (set +o pipefail; read_dump | head -c 65536 | grep -qiE '^(CREATE TABLE|INSERT INTO|-- MariaDB dump|-- MySQL dump)') ||
              fail "$(basename "$f") does not look like a SQL dump (no CREATE TABLE or INSERT INTO near the start)."

            echo "> Safety backup of the current $db first"
            install -d -m 700 {{BackupDirectory}}
            safety={{BackupDirectory}}/$db-before-import-$(date +%Y%m%d-%H%M%S).sql.gz
            if mariadb -N "$db" -e 'SELECT 1' >/dev/null 2>&1; then
              (umask 077; mariadb-dump --single-transaction --quick --routines --triggers --events --hex-blob --databases "$db" | gzip -6 > "$safety")
              echo "  $safety ($(du -h "$safety" | cut -f1))"
            else
              echo "  $db does not exist yet: nothing to back up"
              safety=
            fi

            d={{EmulatorInstaller.Directory}}
            log={{EmulatorInstaller.LogDirectory}}/emulator.log
            {{EmulatorInstaller.ControlFunctions}}
            emulator=
            if [ -x "$d/emulator" ]; then
              emulator=yes
              echo "> Stopping the emulator"
              stop_emulator
            fi

            charset=$(mariadb -N -e "SELECT CONCAT(default_character_set_name, ' ', default_collation_name) FROM information_schema.schemata WHERE schema_name = '$db'")
            set -- ${charset:-utf8mb4 utf8mb4_unicode_ci}
            echo "> Recreating $db (character set $1, collation $2)"
            mariadb -e "DROP DATABASE IF EXISTS \`$db\`; CREATE DATABASE \`$db\` CHARACTER SET $1 COLLATE $2"

            echo "> Importing $(basename "$f") ($(du -h "$f" | cut -f1)) into $db"
            start=$(date +%s)
            if ! read_dump | sed -E "/^(CREATE DATABASE|USE) /s/\`[^\`]+\`/\`$db\`/" | mariadb "$db"; then
              echo "  The import stopped."
              [ -n "$safety" ] && echo "  The database as it was before is in $safety (Import full database with that file restores it)."
              fail "Importing $(basename "$f") failed (see above). The emulator was not started."
            fi
            echo "  $(mariadb -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$db'") tables in $(( $(date +%s) - start )) seconds"

            if [ -d {{AtomCmsInstaller.Directory}}/vendor ]; then
              echo "> AtomCMS: php artisan cache:clear"
              (cd {{AtomCmsInstaller.Directory}} && runuser -u www-data -- php artisan cache:clear --no-interaction >/dev/null 2>&1) || true
            fi
            if [ -n "$emulator" ]; then
              echo "> Starting the emulator (it updates the database to its version)"
              start_emulator
              wait_loaded
            fi
            """;
        return RemoteScript.RunAsync(connection, script, log, "Importing the database failed", token);
    }

    /// <summary>apt-get update, upgrade and dist-upgrade, keeping changed configuration files. Returns whether a reboot is needed.</summary>
    public static async Task<bool> UpdateSystemAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        const string Options = "-y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold";
        string script = RemoteScript.Prelude + $$"""
            set -o pipefail
            echo "> apt-get update"
            apt_update 2>&1 | grep -E '^(Get|Err|E:|W:)' | grep -vE '^Get:[0-9]+ .*(Translation|Packages|Contents)' || true
            for action in upgrade dist-upgrade; do
              echo "> apt-get $action"
              rc=$(mktemp)
              (apt-get {{Options}} $action 2>&1; echo $? > "$rc") | grep -E '^(Setting up|Removing|[0-9]+ upgraded|E:|W:)' | sed 's/^/  /' || true
              code=$(cat "$rc"); rm -f "$rc"
              [ "$code" = 0 ] || fail "apt-get $action stopped with exit code $code."
            done
            if [ -f /var/run/reboot-required ]; then
              echo "  A reboot is needed to finish the update ($(sort -u /var/run/reboot-required.pkgs 2>/dev/null | head -5 | tr '\n' ' '))"
              echo "REBOOT=yes"
            fi
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Updating the system failed", ["REBOOT"], token);
        return result.Yes("REBOOT");
    }

    /// <summary>The emulator's git commit and the version of its built JAR.</summary>
    public static async Task<(string? Commit, string? Version)> EmulatorVersionAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            d={{EmulatorInstaller.Repository}}
            echo "commit=$(git -c safe.directory="$d" -C "$d" rev-parse --short HEAD 2>/dev/null)"
            jar=$(ls -t {{EmulatorInstaller.Directory}}/target/Polaris-*-jar-with-dependencies.jar {{EmulatorInstaller.Directory}}/target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)
            echo "version=$(basename "$jar" -jar-with-dependencies.jar 2>/dev/null)"
            echo "java=$({{EmulatorInstaller.FindJava}})"
            """;
        var values = new ScriptValues((await connection.RunAsRootAsync(script, token)).Output);
        return (values["commit"], values["version"] is { Length: > 0 } v ? v : null);
    }

    public static async Task<string> JavaHomeAsync(ServerConnection connection, CancellationToken token = default)
    {
        string java = (await connection.RunAsRootAsync(EmulatorInstaller.FindJava, token)).Output.Trim();
        return java.Length > 0 ? java : throw new ServerConnectionException("JDK 25 is not installed. Run the Emulator step first.");
    }

    /// <summary>Gives the emulator account its repository back after a git pull as root.</summary>
    public static Task FixEmulatorOwnerAsync(ServerConnection connection, Action<string> log, CancellationToken token = default) =>
        RemoteScript.RunAsync(connection, RemoteScript.Prelude + $"chown -R {EmulatorInstaller.Account}:www-data {EmulatorInstaller.Repository}\n",
            log, "Changing the owner of the emulator files failed", token);
}
