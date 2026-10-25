using AllInOneInstaller.Configuration;

namespace AllInOneInstaller.Remote;

/// <summary>Emulator state on the server before the step runs.</summary>
public sealed record EmulatorState(
    bool RepositoryPresent,
    string? JavaHome,
    bool AccountReady,
    string? Jar,
    bool ConfigPresent,
    bool Running,
    bool ServiceInstalled,
    string? ConfiguredTimezone,
    string? ConfiguredWhitelist,
    int? ConfiguredMemoryMb,
    int RamMb,
    IReadOnlyList<string> Timezones)
{
    public bool IsComplete => RepositoryPresent && JavaHome is not null && AccountReady && Jar is not null && ConfigPresent && Running;
}

public sealed record EmulatorStartResult(string Jar, bool Systemd, int Errors);

/// <summary>Builds and runs the Polaris emulator from /var/www/emulator as emulator.service.</summary>
public static class EmulatorInstaller
{
    public const string Repository = "/var/www/emulator";
    public const string Directory = "/var/www/emulator/Emulator";
    public const string LogDirectory = "/var/log/emu";
    public const string Account = "emulator";
    public const string AccountHome = "/var/lib/emulator";

    /// <summary>Adoptium's signing key, used for Temurin where the distribution has no OpenJDK 25.</summary>
    public const string AdoptiumKeyFingerprint = "3B04D753C9050D9A5D343F39843C48A565F8F04B";

    // The distribution's OpenJDK 25, or Temurin 25.
    public const string FindJava = "ls -d /usr/lib/jvm/java-25-openjdk-* /usr/lib/jvm/temurin-25-jdk-* 2>/dev/null | head -1";

    // Without systemd, look for the process instead.
    private const string RunningCheck =
        "if [ -d /run/systemd/system ]; then systemctl is-active --quiet emulator; else pgrep -u emulator -f 'jar-with-dependencies' >/dev/null; fi";

    // Shell helpers that control the emulator. The calling script must set $d and $log.
    public const string ControlFunctions = $$"""
        emulator_running() { {{RunningCheck}}; }
        stop_emulator() {
          # pkill fails when nothing runs, which would end the script under set -e.
          if [ -d /run/systemd/system ]; then systemctl stop emulator; else pkill -u {{Account}} -f 'jar-with-dependencies' || true; fi
          for i in $(seq 1 30); do emulator_running || return 0; sleep 1; done
          fail "The emulator did not stop within 30 seconds."
        }
        start_emulator() {
          # Start with an empty log so only this start counts.
          [ -f "$log" ] && mv "$log" "$log.$(date +%H%M%S_%d-%m-%Y)"
          if [ -d /run/systemd/system ]; then
            systemctl restart emulator
          else
            stop_emulator
            setsid runuser -u {{Account}} -- "$d/emulator" </dev/null >/dev/null 2>&1 &
          fi
        }
        wait_loaded() {
          for i in $(seq 1 300); do
            sleep 2
            if grep -q 'successfully loaded' "$log" 2>/dev/null; then echo "  loaded after $((i * 2)) seconds"; return 0; fi
            if grep -q 'startup was aborted' "$log" 2>/dev/null || ! emulator_running; then
              [ $i -gt 3 ] || continue
              echo "  --- last lines of $log ---"; tail -25 "$log" 2>/dev/null | sed 's/^/  /'
              fail "The emulator stopped during startup. See $log."
            fi
          done
          fail "The emulator did not finish loading within 10 minutes. See $log."
        }
        """;

    public static async Task<EmulatorState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            d={{Directory}}
            echo "repo=$([ -f "$d/pom.xml" ] && echo yes)"
            echo "java=$({{FindJava}})"
            echo "account=$(id -nG {{Account}} 2>/dev/null | grep -qw www-data && echo yes)"
            echo "jar=$(ls -t "$d"/target/Polaris-*-jar-with-dependencies.jar "$d"/target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)"
            echo "config=$([ -f "$d/config.ini" ] && echo yes)"
            echo "running=$({{RunningCheck}} && echo yes)"
            echo "service=$([ -f /etc/systemd/system/emulator.service ] && echo yes)"
            echo "configtz=$(grep -oE 'serverTimezone=[^&[:space:]]+' "$d/config.ini" 2>/dev/null | head -1 | cut -d= -f2)"
            echo "memory=$(grep -oE '^MEMORY_MB=[0-9]+|Xmx[0-9]+m' "$d/emulator" 2>/dev/null | head -1 | tr -dc 0-9)"
            echo "whitelist=$(mariadb -N -e "SELECT value FROM habbo.emulator_settings WHERE \`key\` = 'ws.whitelist'" 2>/dev/null)"
            echo "ram=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)"
            echo "zones=$( (timedatectl list-timezones 2>/dev/null || (cd /usr/share/zoneinfo && find [A-Z]* -type f | grep -vE '^(posix|right|Etc/)' | sort)) | tr '\n' ' ')"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);
        string[] zones = (values["zones"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new EmulatorState(values.Yes("repo"), values["java"], values.Yes("account"), values["jar"],
            values.Yes("config"), values.Yes("running"), values.Yes("service"),
            values["configtz"], values["whitelist"],
            int.TryParse(values["memory"], out int memory) ? memory : null,
            int.TryParse(values["ram"], out int ram) ? ram : 0,
            zones.Length > 0 ? zones : ["Etc/UTC"]);
    }

    /// <summary>
    /// Installs JDK 25, since the build needs 25 or 26. Uses Adoptium's Temurin where the distribution has none,
    /// as on Debian 11 and 12. Returns JAVA_HOME.
    /// </summary>
    public static async Task<string> InstallJavaAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            echo "> apt-get update"
            apt_update
            if apt-cache show openjdk-25-jdk-headless >/dev/null 2>&1; then
              echo "> apt-get install openjdk-25-jdk-headless"
              apt-get install -y openjdk-25-jdk-headless curl unzip ca-certificates
            else
              echo "> $PRETTY_NAME has no OpenJDK 25: using Eclipse Temurin 25 from Adoptium"
              apt-get install -y --no-install-recommends curl gnupg ca-certificates unzip
              tmp=$(mktemp)
              trap 'rm -f "$tmp"' EXIT
              curl -fsSL https://packages.adoptium.net/artifactory/api/gpg/key/public -o "$tmp" || fail "Could not download the Adoptium signing key."
              fprs=$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '/^pub/{p=1; next} /^fpr/ && p {print $10; p=0}')
              [ "$fprs" = "{{AdoptiumKeyFingerprint}}" ] || fail "The downloaded key is not the Adoptium key {{AdoptiumKeyFingerprint}} (found: $fprs)."
              echo "  key {{AdoptiumKeyFingerprint}} ok"
              install -d -m 0755 /etc/apt/keyrings
              gpg --dearmor --yes -o /etc/apt/keyrings/adoptium.gpg "$tmp"
              echo "deb [signed-by=/etc/apt/keyrings/adoptium.gpg] https://packages.adoptium.net/artifactory/deb $codename main" > /etc/apt/sources.list.d/adoptium.list
              apt_update
              apt-get install -y temurin-25-jdk
            fi
            java_home=$({{FindJava}})
            [ -n "$java_home" ] || fail "JDK 25 is not in /usr/lib/jvm after installing."
            "$java_home/bin/java" -version 2>&1 | head -1
            echo "JAVA_HOME=$java_home"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing Java failed", ["JAVA_HOME"], token);
        return result["JAVA_HOME"] ?? "";
    }

    /// <summary>Creates a system account without a login shell. Its home holds Maven's cache.</summary>
    public static Task CreateAccountAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            getent group www-data >/dev/null || groupadd --system www-data
            if ! id {{Account}} >/dev/null 2>&1; then
              useradd --system --user-group --home-dir {{AccountHome}} --create-home --shell /usr/sbin/nologin {{Account}}
              echo "  account {{Account}} created"
            fi
            usermod -aG www-data {{Account}}
            echo "  $(id {{Account}})"
            chown -R {{Account}}:www-data {{Repository}}
            echo "  {{Repository}} belongs to {{Account}}:www-data"
            # setgid so log files created here belong to group www-data and the website can read them.
            install -d -m 2750 -o {{Account}} -g www-data {{LogDirectory}}
            chmod 2750 {{LogDirectory}}
            chown {{Account}}:www-data {{LogDirectory}}/emulator.log* 2>/dev/null || true
            echo "  {{LogDirectory}}: $(stat -c '%A %U:%G' {{LogDirectory}})"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Creating the emulator account failed", token);
    }

    /// <summary>Runs Emulator/build.sh as the emulator account. Returns the built JAR.</summary>
    public static async Task<string> BuildAsync(ServerConnection connection, string javaHome, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            d={{Directory}}
            # mvnw downloads Maven silently and without a timeout, which can hang forever on a server with broken IPv6.
            # Download the version it asks for here instead, with visible output and timeouts.
            props="$d/.mvn/wrapper/maven-wrapper.properties"
            url=$(sed -n 's/^distributionUrl=//p' "$props" 2>/dev/null | tr -d '\r')
            sum=$(sed -n 's/^distributionSha256Sum=//p' "$props" 2>/dev/null | tr -d '\r')
            maven=
            if [ -n "$url" ]; then
              name=$(basename "$url" -bin.zip)
              maven=/opt/$name/bin/mvn
              if [ ! -x "$maven" ]; then
                echo "> Downloading $name (only the first build)"
                echo "  $url"
                tmp=$(mktemp -d)
                # New Maven releases can reach the Apache download servers before Maven Central, so try those too.
                version=${name#apache-maven-}
                apache="maven/maven-${version%%.*}/$version/binaries/$name-bin.zip"
                got=
                for from in "$url" "https://dlcdn.apache.org/$apache" "https://archive.apache.org/dist/$apache"; do
                  if download "$from" "$tmp/maven.zip"; then got=$from; break; fi
                  echo "  Not available from $(echo "$from" | cut -d/ -f3), trying the next server ..."
                done
                [ -n "$got" ] || { rm -rf "$tmp"; fail "Could not download $name. Check that the server can reach repo.maven.apache.org and dlcdn.apache.org (DNS, firewall, proxy)."; }
                [ "$got" = "$url" ] || echo "  from $got"
                echo "  $(du -h "$tmp/maven.zip" | cut -f1) downloaded"
                if [ -n "$sum" ] && ! echo "$sum  $tmp/maven.zip" | sha256sum -c --status; then
                  rm -rf "$tmp"; fail "The downloaded Maven does not match the sha256 in maven-wrapper.properties."
                fi
                rm -rf "/opt/$name"
                unzip -q "$tmp/maven.zip" -d /opt
                rm -rf "$tmp"
                [ -x "$maven" ] || fail "$maven is missing after unpacking."
              fi
              echo "  $("$maven" -v 2>/dev/null | head -1 | sed 's/\x1b\[[0-9;]*m//g')"
            fi
            cat > "$d/build.sh" <<'BUILD'
            #!/bin/sh
            # Builds the Polaris emulator into target/ (written by All-in-1 Installer).
            #   ./build.sh               build without the unit tests (about 1 minute)
            #   ./build.sh --with-tests  also run the 4000+ unit tests (about 4 minutes)
            # pom.xml needs Maven 3.9.11+ and JDK 25, newer than most distributions ship: the installer puts the
            # Maven version of the Maven Wrapper in /opt; without it, mvnw downloads it.
            set -e
            cd "$(dirname "$0")"
            export JAVA_HOME=JAVA_HOME_HERE
            mvn=MAVEN_HERE
            [ -x "$mvn" ] || mvn=./mvnw
            tests=-DskipTests
            [ "$1" = "--with-tests" ] && tests=
            exec "$mvn" -B -ntp clean package $tests -f pom.xml
            BUILD
            sed -i -e "s#JAVA_HOME_HERE#{{javaHome}}#" -e "s#MAVEN_HERE#${maven:-./mvnw}#" "$d/build.sh"
            chown {{Account}}:www-data "$d/build.sh"
            chmod 755 "$d/build.sh" "$d/mvnw"
            echo "> $d/build.sh (as {{Account}})"
            [ -d {{AccountHome}}/.m2/repository ] || echo "  The first build downloads the libraries (about 150 MB): this takes a few minutes."
            cd "$d"
            # Maven prints little while it downloads, so report progress every 30 s and give up after 30 minutes.
            start=$(date +%s)
            timeout 1800 runuser -u {{Account}} -- env HOME={{AccountHome}} "$d/build.sh" &
            build=$!
            (
              while sleep 30 </dev/null >/dev/null 2>&1; do
                kill -0 $build 2>/dev/null || exit 0
                echo "  ... still building: $(( ($(date +%s) - start) / 60 )) min, $(du -sm {{AccountHome}}/.m2 2>/dev/null | cut -f1) MB in ~/.m2"
              done
            ) &
            beat=$!
            if wait $build; then rc=0; else rc=$?; fi
            kill $beat 2>/dev/null || true
            wait $beat 2>/dev/null || true
            [ $rc -ne 124 ] || fail "The build did not finish within 30 minutes. Run $d/build.sh on the server to see where it stops."
            [ $rc -eq 0 ] || fail "build.sh stopped with exit code $rc (see the Maven output above)."
            jar=$(ls -t target/Polaris-*-jar-with-dependencies.jar target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)
            [ -n "$jar" ] || fail "The build finished, but there is no *-jar-with-dependencies.jar in $d/target."
            echo "JAR=$d/$jar"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Building the emulator failed", ["JAR"], token);
        return result["JAR"] ?? "";
    }

    /// <summary>
    /// Secrets go over stdin. An existing config.ini keeps everything except the database settings and
    /// placeholder secrets.
    /// </summary>
    public static Task ConfigureAsync(ServerConnection connection, DatabaseCredentials credentials, string timezone, int memoryMb,
        string javaHome, Action<string> log, CancellationToken token = default)
    {
        string user = Sql.Literal(credentials.EmulatorUser);
        string sql = $"""
            CREATE USER IF NOT EXISTS {user}@'localhost' IDENTIFIED BY {Sql.Literal(credentials.EmulatorPassword)};
            ALTER USER {user}@'localhost' IDENTIFIED BY {Sql.Literal(credentials.EmulatorPassword)};
            CREATE SCHEMA IF NOT EXISTS `{credentials.Database.Replace("`", "``")}`;
            GRANT ALL PRIVILEGES ON `{credentials.Database.Replace("`", "``")}`.* TO {user}@'localhost';
            FLUSH PRIVILEGES;
            """;
        // "key=value" replaces the line in config.ini. "?key=value" only replaces an empty or change-me value.
        string overrides = string.Join('\n',
            "db.hostname=127.0.0.1",
            $"db.port={credentials.Port}",
            $"db.database={credentials.Database}",
            $"db.username={credentials.EmulatorUser}",
            $"db.password={credentials.EmulatorPassword}",
            $"db.params=?characterEncoding=utf8&useSSL=false&serverTimezone={timezone}",
            $"?nitro.secure.master_key={DatabaseCredentials.NewPassword()}{DatabaseCredentials.NewPassword()}",
            $"?login.remember.jwt.secret={DatabaseCredentials.NewPassword()}{DatabaseCredentials.NewPassword()}");
        string stdin = sql + "\n-----OVERRIDES-----\n" + overrides + "\n";

        string script = RemoteScript.Prelude + $$"""
            umask 077
            work=$(mktemp -d)
            trap 'rm -rf "$work"' EXIT
            cat > "$work/input"
            awk -v dir="$work" '/^-----OVERRIDES-----$/ {part=1; next} { print > (dir "/" (part ? "overrides" : "sql")) }' "$work/input"

            echo "> MariaDB account {{credentials.EmulatorUser}}@localhost (all privileges on {{credentials.Database}})"
            mariadb < "$work/sql"

            d={{Directory}}
            base="$d/config.ini"
            if [ -f "$base" ]; then
              cp -p "$base" "$base.bak-$(date +%Y%m%d-%H%M%S)"
              echo "> Updating the database settings in $base (a backup was made)"
            else
              base="{{Repository}}/config example/config.ini.example"
              [ -f "$base" ] || fail "There is no config.ini.example in {{Repository}}/config example."
              echo "> Writing $d/config.ini from config.ini.example"
            fi
            awk -F'=' '
              FNR == NR { soft = ($0 ~ /^\?/); line = soft ? substr($0, 2) : $0; k = substr(line, 1, index(line, "=") - 1)
                          ov[k] = line; isSoft[k] = soft; next }
              /^[ \t]*#/ || index($0, "=") == 0 { print; next }
              { k = $1; gsub(/[ \t]+$/, "", k); v = substr($0, index($0, "=") + 1); gsub(/^[ \t]+|[ \t]+$/, "", v)
                if ((k in ov) && (!isSoft[k] || v == "" || v ~ /change-me/)) { print ov[k]; done[k] = 1 }
                else { print; if (k in ov) done[k] = 1 } }
              END { for (k in ov) if (!(k in done)) print ov[k] }
            ' "$work/overrides" "$base" > "$work/config.ini"
            install -m 0640 -o {{Account}} -g www-data "$work/config.ini" "$d/config.ini"
            grep -E '^db\.(hostname|port|database|username|params)=' "$d/config.ini" | sed 's/^/  /'

            echo "> Writing the start script $d/emulator"
            cat > "$d/emulator" <<'START'
            #!/bin/sh
            # Starts the Polaris emulator; used by emulator.service (written by All-in-1 Installer).
            # The previous log is kept as emulator.log.<time>; kept logs older than 30 days are removed.
            JAVA_HOME=JAVA_HOME_HERE
            MEMORY_MB=MEMORY_HERE
            LOG_DIR=/var/log/emu
            # Logs and backups: writable by emulator, readable by group www-data, not by others.
            umask 027
            cd "$(dirname "$0")"
            jar=$(ls -t target/Polaris-*-jar-with-dependencies.jar target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)
            [ -n "$jar" ] || { echo "No emulator jar in $(pwd)/target: run ./build.sh first." >&2; exit 1; }
            if [ -f "$LOG_DIR/emulator.log" ]; then
              mv "$LOG_DIR/emulator.log" "$LOG_DIR/emulator.log.$(date +%H%M_%d-%m-%Y)"
            fi
            find "$LOG_DIR" -name 'emulator.log.*' -mtime +30 -delete 2>/dev/null
            exec "$JAVA_HOME/bin/java" -Dfile.encoding=UTF8 -Xmx${MEMORY_MB}m -jar "$jar" >> "$LOG_DIR/emulator.log" 2>&1
            START
            sed -i -e "s#JAVA_HOME_HERE#{{javaHome}}#" -e "s#MEMORY_HERE#{{memoryMb}}#" "$d/emulator"
            chown {{Account}}:www-data "$d/emulator"
            chmod 755 "$d/emulator"

            echo "> Writing /etc/systemd/system/emulator.service"
            cat > /etc/systemd/system/emulator.service <<'UNIT'
            [Unit]
            Description=Polaris emulator
            # The database must be running before the emulator starts.
            After=network-online.target mariadb.service
            Wants=network-online.target
            Requires=mariadb.service

            [Service]
            User=emulator
            Group=emulator
            SupplementaryGroups=www-data
            # config.ini, build.sh and the start script live here; target/ only holds the build output.
            WorkingDirectory=/var/www/emulator/Emulator
            ExecStart=/var/www/emulator/Emulator/emulator
            SuccessExitStatus=143
            TimeoutStopSec=30
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target
            UNIT
            chmod 644 /etc/systemd/system/emulator.service
            if [ -d /run/systemd/system ]; then
              systemctl daemon-reload
              systemctl enable emulator
            else
              echo "  (no systemd on this machine: the emulator is started directly, not at boot)"
            fi
            """;
        return RemoteScript.RunAsync(connection, script, log, "Configuring the emulator failed", token, stdin);
    }

    /// <summary>Camera and badge imager settings for emulator_settings.</summary>
    public static (string Key, string Value)[] ImagerSettings(string siteUrl) =>
    [
        ("camera.url", $"{siteUrl}/gamedata/camera/"),
        ("imager.location.output.camera", HotelSite.Camera),
        ("imager.location.output.thumbnail", HotelSite.Camera + "/thumb/"),
        ("camera.render.mode", "auto"),
        ("imager.location.output.badges", OctaneInstaller.Gamedata + "/c_images/album1584/"),
        ("imager.location.badgeparts", OctaneInstaller.Gamedata + "/c_images/groupbadge/badgeparts/")
    ];

    /// <summary>"key=value,..." sorted by key, as the server's GROUP_CONCAT of these keys prints it.</summary>
    public static string ImagerSettingsLine(string siteUrl) =>
        string.Join(",", ImagerSettings(siteUrl).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));

    public static string ImagerKeysSql => string.Join(", ", ImagerSettings("").Select(s => Sql.Literal(s.Key)));

    /// <summary>Writes <see cref="ImagerSettings"/> via <see cref="SetSettingsAsync"/> and creates their folders.</summary>
    public static Task<bool> ConfigureImagerAsync(ServerConnection connection, string siteUrl, Action<string> log,
        CancellationToken token = default)
    {
        string g = OctaneInstaller.Gamedata;
        string folders = $$"""
            install -d -m 2775 -o www-data -g www-data {{HotelSite.Camera}} {{HotelSite.Camera}}/thumb {{g}}/c_images {{g}}/c_images/album1584 \
              {{g}}/c_images/groupbadge {{g}}/c_images/groupbadge/badge {{g}}/c_images/groupbadge/badgeparts
            """;
        return SetSettingsAsync(connection, ImagerSettings(siteUrl), "camera and badge imager", log, folders, token);
    }

    /// <summary>
    /// Stops a running emulator while writing so it cannot write its own values back. Does nothing when the values
    /// are already set, and returns false when the emulator is not set up yet.
    /// </summary>
    public static async Task<bool> SetSettingsAsync(ServerConnection connection, IReadOnlyList<(string Key, string Value)> settings,
        string what, Action<string> log, string setup = "", CancellationToken token = default)
    {
        string keys = string.Join(", ", settings.Select(s => Sql.Literal(s.Key)));
        string sql = string.Concat(settings.Select(s =>
            $"DELETE FROM emulator_settings WHERE `key` = {Sql.Literal(s.Key)};\n" +
            $"INSERT INTO emulator_settings (`key`, `value`) VALUES ({Sql.Literal(s.Key)}, {Sql.Literal(s.Value)});\n"));
        string wanted = string.Join(",", settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));

        string script = RemoteScript.Prelude + $$"""
            d={{Directory}}
            log={{LogDirectory}}/emulator.log
            db=$(grep -sE '^db.database=' "$d/config.ini" | cut -d= -f2)
            if [ -z "$db" ] || ! mariadb -N "$db" -e "SELECT 1 FROM emulator_settings LIMIT 1" >/dev/null 2>&1; then
              echo "  The emulator is not set up yet (no emulator_settings): run the Emulator step, then this step again."
              echo "SETTINGS=no"
              exit 0
            fi
            {{setup}}
            current=$(mariadb -N "$db" -e "SELECT GROUP_CONCAT(\`key\`, '=', \`value\` ORDER BY \`key\` SEPARATOR ',') FROM emulator_settings WHERE \`key\` IN ({{keys}})")
            if [ "$current" = '{{wanted}}' ]; then
              echo "  {{what}} settings are already set"
              echo "SETTINGS=yes"
              exit 0
            fi

            {{ControlFunctions}}
            was_running=
            if emulator_running; then
              was_running=yes
              echo "> Stopping the emulator"
              stop_emulator
            fi

            echo "> emulator_settings: {{string.Join(", ", settings.Select(s => s.Key))}}"
            mariadb "$db" <<'SQL'
            {{sql}}
            SQL
            mariadb -N "$db" -e "SELECT \`key\`, \`value\` FROM emulator_settings WHERE \`key\` IN ({{keys}}) ORDER BY \`key\`" | sed 's/^/    /; s/\t/ = /'

            if [ -n "$was_running" ]; then
              echo "> Starting the emulator"
              start_emulator
              wait_loaded
            else
              echo "  The emulator was not running: the settings apply when it starts."
            fi
            echo "SETTINGS=yes"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log,
            $"Setting the {what} in emulator_settings failed", ["SETTINGS"], token);
        return result.Yes("SETTINGS");
    }

    /// <summary>Returns null when the key is unset or the emulator is not set up.</summary>
    public static async Task<string?> ReadSettingAsync(ServerConnection connection, string key, CancellationToken token = default)
    {
        string script = $$"""
            db=$(grep -sE '^db.database=' {{Directory}}/config.ini | cut -d= -f2)
            [ -n "$db" ] && mariadb -N "$db" -e "SELECT \`value\` FROM emulator_settings WHERE \`key\` = {{Sql.Literal(key)}}" 2>/dev/null
            """;
        string value = (await connection.RunAsRootAsync(script, token)).Output.Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Restarts the emulator and waits until it has loaded. The first start creates the database. Then sets
    /// console.mode and the websocket whitelist, restarting again if they changed.
    /// </summary>
    public static async Task<EmulatorStartResult> StartAsync(ServerConnection connection, string whitelist, Action<string> log,
        CancellationToken token = default)
    {
        string settings = $"""
            UPDATE emulator_settings SET `value` = '0' WHERE `key` = 'console.mode';
            UPDATE emulator_settings SET `value` = {Sql.Literal(whitelist)} WHERE `key` IN ('ws.whitelist', 'websockets.whitelist');
            INSERT INTO emulator_settings (`key`, `value`) SELECT 'ws.whitelist', {Sql.Literal(whitelist)} FROM DUAL
              WHERE NOT EXISTS (SELECT 1 FROM emulator_settings WHERE `key` = 'ws.whitelist');

            """;
        string script = RemoteScript.Prelude + $$"""
            umask 077
            sql=$(mktemp)
            trap 'rm -f "$sql"' EXIT
            cat > "$sql"
            d={{Directory}}
            log={{LogDirectory}}/emulator.log
            db=$(grep -E '^db.database=' "$d/config.ini" | cut -d= -f2)

            {{ControlFunctions}}

            echo "> Starting the emulator (the first start creates the database tables)"
            start_emulator
            wait_loaded

            echo "> emulator_settings: console.mode = 0, ws.whitelist"
            before=$(mariadb -N "$db" -e "SELECT GROUP_CONCAT(\`key\`, '=', \`value\` ORDER BY \`key\`) FROM emulator_settings WHERE \`key\` IN ('console.mode', 'ws.whitelist', 'websockets.whitelist')")
            mariadb "$db" < "$sql"
            after=$(mariadb -N "$db" -e "SELECT GROUP_CONCAT(\`key\`, '=', \`value\` ORDER BY \`key\`) FROM emulator_settings WHERE \`key\` IN ('console.mode', 'ws.whitelist', 'websockets.whitelist')")
            echo "  $after"
            if [ "$before" != "$after" ]; then
              echo "> Restarting the emulator to use the new settings"
              start_emulator
              wait_loaded
            fi

            grep -E 'Started (Game|RCON) Server' "$log" | sed 's/^.*| /  /'
            ss -ltn 2>/dev/null | grep -q ':3000 ' || fail "The emulator is running, but nothing listens on port 3000."
            ufw_allow 3000/tcp
            echo "ERRORS=$(grep -c ' ERROR ' "$log")"
            echo "SYSTEMD=$([ -d /run/systemd/system ] && echo yes)"
            echo "JAR=$d/$(cd "$d" && ls -t target/Polaris-*-jar-with-dependencies.jar target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Starting the emulator failed",
            ["ERRORS", "SYSTEMD", "JAR"], token, settings);
        return new EmulatorStartResult(result["JAR"] ?? "", result.Yes("SYSTEMD"), result.Int("ERRORS"));
    }
}
