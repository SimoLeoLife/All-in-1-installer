using AllInOneInstaller.Configuration;

namespace AllInOneInstaller.Remote;

/// <summary>MariaDB as found on the server before the MariaDB step runs.</summary>
public sealed record MariaDbState(
    string Codename,
    string? InstalledVersion,
    bool MySqlInstalled,
    bool Running,
    string? BindAddress,
    bool DatabaseExists,
    bool RootRemoteExists,
    bool CmsExists)
{
    // Without the "1:" epoch.
    public string? DisplayVersion => InstalledVersion?.Split(':').Last();

    // mariadb.org packages carry "+maria~" in their version, distribution packages do not.
    public bool FromMariaDbOrg => InstalledVersion?.Contains("+maria", StringComparison.Ordinal) == true;

    public bool IsTargetBranch => FromMariaDbOrg && InstalledVersion!.Split(':').Last().StartsWith(MariaDbInstaller.Branch + ".", StringComparison.Ordinal);

    // A distribution or other-branch MariaDB whose data would be upgraded.
    public bool NeedsUpgrade => InstalledVersion is not null && !IsTargetBranch;

    public bool UsersExist => RootRemoteExists && CmsExists;

    // Saved passwords are checked separately.
    public bool IsComplete => IsTargetBranch && Running && BindAddress == "0.0.0.0" && DatabaseExists && UsersExist;
}

/// <summary>
/// Installs MariaDB 12.3 from mariadb.org, applies the hotel's mariadb-secure-installation answers, listens on all
/// interfaces and creates root@'%', cms@'%' and the habbo database.
/// </summary>
public static class MariaDbInstaller
{
    public const string Branch = "12.3";

    // "MariaDB Signing Key <signing-key@mariadb.org>", which signs the mariadb.org repositories.
    public const string KeyFingerprint = "177F4010FE56CA3336300305F1656F24C74CD1D8";

    public const string ServerConfig = "/etc/mysql/mariadb.conf.d/50-server.cnf";

    private const string CmsPrivileges =
        "EXECUTE, SELECT, SHOW VIEW, ALTER, ALTER ROUTINE, CREATE, CREATE ROUTINE, CREATE TEMPORARY TABLES, CREATE VIEW, " +
        "DELETE, DROP, EVENT, INDEX, INSERT, REFERENCES, TRIGGER, UPDATE, LOCK TABLES";

    // Copies stdin into a root-only temp file "$secret" so secrets never appear on a command line.
    private const string ReadSecret = """
        umask 077
        secret=$(mktemp)
        trap 'rm -f "$secret"' EXIT
        cat > "$secret"
        """ + "\n";

    public static async Task<MariaDbState> InspectAsync(ServerConnection connection, CancellationToken token = default)
    {
        const string script = """
            . /etc/os-release
            echo "codename=${VERSION_CODENAME:-$(lsb_release -cs 2>/dev/null)}"
            echo "version=$(dpkg-query -W -f='${db:Status-Abbrev}${Version}' mariadb-server 2>/dev/null | awk '/^ii/{print substr($0,4)}')"
            echo "mysql=$(dpkg-query -W -f='${db:Status-Abbrev}${Package}\n' mysql-server 'mysql-server-8*' 'mysql-community-server' 2>/dev/null | grep -q '^ii' && echo yes)"
            if pgrep -x mariadbd >/dev/null || pgrep -x mysqld >/dev/null; then
              echo "running=yes"
              echo "bind=$(mariadb -N -e 'SELECT @@bind_address' 2>/dev/null)"
              echo "database=$(mariadb -N -e "SELECT 1 FROM information_schema.schemata WHERE schema_name='habbo'" 2>/dev/null)"
              echo "rootremote=$(mariadb -N -e "SELECT 1 FROM mysql.user WHERE user='root' AND host='%'" 2>/dev/null)"
              echo "cms=$(mariadb -N -e "SELECT 1 FROM mysql.user WHERE user='cms' AND host='%'" 2>/dev/null)"
            fi
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        if (!result.Succeeded) throw new ServerConnectionException("Could not inspect MariaDB on the server: " + result.Error.Trim());

        var values = new ScriptValues(result.Output);
        return new MariaDbState(values["codename"] ?? "", values["version"], values.Yes("mysql"), values.Yes("running"), values["bind"],
            values["database"] == "1", values["rootremote"] == "1", values["cms"] == "1");
    }

    // Checks the signing key against KeyFingerprint and pins the repository above the distribution's packages.
    public static Task AddRepositoryAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get update"
            apt_update
            echo "> Installing prerequisites"
            apt-get install -y --no-install-recommends curl gnupg ca-certificates lsb-release
            url="https://dlm.mariadb.com/repo/mariadb-server/{{Branch}}/repo/$ID"
            curl -fsSIL "$url/dists/$codename/Release" >/dev/null || fail "mariadb.org has no MariaDB {{Branch}} packages for $PRETTY_NAME ($codename)."

            echo "> Downloading and checking the MariaDB signing key"
            tmp=$(mktemp)
            trap 'rm -f "$tmp"' EXIT
            curl -fsSL https://mariadb.org/mariadb_release_signing_key.pgp -o "$tmp" || fail "Could not download the MariaDB signing key."
            fprs=$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '/^pub/{p=1; next} /^fpr/ && p {print $10; p=0}')
            [ "$fprs" = "{{KeyFingerprint}}" ] || fail "The downloaded key is not the MariaDB signing key {{KeyFingerprint}} (found: $fprs)."
            echo "  key {{KeyFingerprint}} ok"
            gpg --dearmor --yes -o /usr/share/keyrings/mariadb-keyring.gpg "$tmp"

            echo "> Adding $url ($codename)"
            echo "deb [signed-by=/usr/share/keyrings/mariadb-keyring.gpg] $url $codename main" > /etc/apt/sources.list.d/mariadb.list
            printf 'Package: *\nPin: origin dlm.mariadb.com\nPin-Priority: 900\n' > /etc/apt/preferences.d/99mariadb
            apt_update
            """;
        return RemoteScript.RunAsync(connection, script, log, "Adding the MariaDB repository failed", token);
    }

    /// <summary>Dumps all databases to /root/mariadb-backup-&lt;time&gt;.sql.gz and returns the path.</summary>
    public static async Task<string> BackupAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """

            backup="/root/mariadb-backup-$(date +%Y%m%d-%H%M%S).sql.gz"
            echo "> Backing up all databases to $backup"
            dump=$(command -v mariadb-dump || command -v mysqldump)
            ( umask 077; "$dump" --all-databases --single-transaction --routines --events --triggers | gzip > "$backup" )
            ls -l "$backup"
            echo "BACKUP=$backup"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Backing up the databases failed", ["BACKUP"], token);
        return result["BACKUP"] ?? "";
    }

    /// <summary>Returns the installed package version.</summary>
    public static async Task<string> InstallAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> apt-get install mariadb-server mariadb-client"
            apt-get install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold mariadb-server mariadb-client
            version=$(dpkg-query -W -f='${Version}' mariadb-server)
            case "$version" in *:{{Branch}}.*+maria*) ;; *) fail "Installed mariadb-server $version is not MariaDB {{Branch}} from mariadb.org." ;; esac

            # When package scripts may not restart services, the old server keeps running the replaced binary.
            # Shut it down cleanly, otherwise the new client cannot talk to it.
            for pid in $(pgrep -x mariadbd; pgrep -x mysqld); do
              if readlink "/proc/$pid/exe" | grep -q '(deleted)'; then
                echo "  Stopping the old MariaDB server (pid $pid) that still runs the replaced program"
                kill -TERM "$pid"
                for i in $(seq 1 60); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
                kill -0 "$pid" 2>/dev/null && fail "The old MariaDB server (pid $pid) did not stop."
              fi
            done

            echo "> Starting MariaDB"
            if [ -d /run/systemd/system ]; then
              systemctl enable mariadb
              systemctl restart mariadb
            else
              service mariadb restart
            fi
            for i in $(seq 1 30); do mariadb-admin ping >/dev/null 2>&1 && break; sleep 1; done
            mariadb-admin ping || fail "MariaDB did not start."
            running=$(mariadb -N -e 'SELECT VERSION()')
            echo "  running: $running"
            case "$running" in {{Branch}}.*) ;; *) fail "The running server is $running, not MariaDB {{Branch}}." ;; esac
            echo "> mariadb-upgrade"
            mariadb-upgrade
            echo "VERSION=${version#*:}"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Installing MariaDB failed", ["VERSION"], token);
        return result["VERSION"] ?? "";
    }

    /// <summary>
    /// Applies mariadb-secure-installation with the hotel's answers (n, n, Y, n, Y, Y), then sets bind-address = 0.0.0.0.
    /// The firewall keeps 3306 closed.
    /// </summary>
    public static Task SecureAndListenAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""

            echo "> Removing anonymous users"
            mariadb -N -e "SELECT CONCAT(QUOTE(User), '@', QUOTE(Host)) FROM mysql.user WHERE User = ''" | while read -r account; do
              mariadb -e "DROP USER $account" && echo "  dropped $account"
            done
            echo "> Removing the test database"
            mariadb -e "DROP DATABASE IF EXISTS test; DELETE FROM mysql.db WHERE Db = 'test' OR Db = 'test\\_%'; FLUSH PRIVILEGES;"
            echo "  done (root keeps its unix_socket login; remote root stays allowed)"

            echo "> Setting bind-address = 0.0.0.0 in {{ServerConfig}}"
            f={{ServerConfig}}
            [ -f "$f" ] || fail "$f does not exist."
            cp -p "$f" "$f.bak-$(date +%Y%m%d-%H%M%S)"
            if grep -qE '^[#[:space:]]*bind-address' "$f"; then
              sed -i -E 's|^[#[:space:]]*bind-address[[:space:]]*=.*|bind-address            = 0.0.0.0|' "$f"
            else
              sed -i '/^\[mysqld\]/a bind-address            = 0.0.0.0' "$f"
            fi
            grep -E '^bind-address' "$f"

            echo "> Restarting MariaDB"
            if [ -d /run/systemd/system ]; then systemctl restart mariadb; else service mariadb restart; fi
            for i in $(seq 1 30); do mariadb-admin ping >/dev/null 2>&1 && break; sleep 1; done
            bind=$(mariadb -N -e 'SELECT @@bind_address')
            echo "  listening on $bind:$(mariadb -N -e 'SELECT @@port')"
            [ "$bind" = "0.0.0.0" ] || fail "MariaDB listens on '$bind' instead of 0.0.0.0 (another config file may set bind-address)."
            # Port 3306 is never opened in the firewall. Tools like HeidiSQL connect through an SSH tunnel with a key.
            """;
        return RemoteScript.RunAsync(connection, script, log, "Securing MariaDB failed", token);
    }

    // root@localhost keeps its socket login and also accepts the password. An SSH tunnel arrives from 127.0.0.1,
    // which MariaDB matches to localhost rather than root@'%'.
    public static string LocalRootPasswordSql(DatabaseCredentials credentials) =>
        $"ALTER USER {Sql.Literal(credentials.RootUser)}@'localhost' IDENTIFIED VIA unix_socket OR mysql_native_password USING PASSWORD({Sql.Literal(credentials.RootPassword)});";

    // For servers set up before LocalRootPasswordSql existed.
    public static Task AllowTunnelRootLoginAsync(ServerConnection connection, DatabaseCredentials credentials, Action<string> log,
        CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + ReadSecret + $$"""

            echo "> root@localhost: socket login, and the root password for SSH tunnels (HeidiSQL)"
            mariadb < "$secret"
            mariadb --protocol=socket -N -e 'SELECT CURRENT_USER()' >/dev/null || fail "The socket login of root stopped working."
            echo "  done"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Changing root@localhost failed", token,
            stdin: LocalRootPasswordSql(credentials) + "\nFLUSH PRIVILEGES;\n");
    }

    /// <summary>
    /// Gives root@localhost (socket login kept) and root@'%' when it exists <paramref name="credentials"/>' root password.
    /// When root cannot log in through the socket any more, MariaDB is restarted for a moment with --skip-grant-tables
    /// and --skip-networking to change it, then started normally again.
    /// </summary>
    public static Task ResetRootPasswordAsync(ServerConnection connection, DatabaseCredentials credentials, Action<string> log,
        CancellationToken token = default)
    {
        string root = Sql.Literal(credentials.RootUser), password = Sql.Literal(credentials.RootPassword);
        // FLUSH PRIVILEGES first: without grant tables, ALTER USER only works after it has loaded them.
        string sql = $"""
            FLUSH PRIVILEGES;
            {LocalRootPasswordSql(credentials)}
            ALTER USER IF EXISTS {root}@'%' IDENTIFIED BY {password};
            FLUSH PRIVILEGES;

            """;
        string script = RemoteScript.Prelude + ReadSecret + """

            command -v mariadb >/dev/null || fail "MariaDB is not installed on this server."
            service_mariadb() { if [ -d /run/systemd/system ]; then systemctl "$1" mariadb; else service mariadb "$1"; fi; }
            answers() { for i in $(seq 1 30); do mariadb -N -e 'SELECT 1' >/dev/null 2>&1 && return 0; sleep 1; done; return 1; }
            if ! pgrep -x mariadbd >/dev/null && ! pgrep -x mysqld >/dev/null; then
              echo "> MariaDB is not running: starting it"
              service_mariadb start || true
              answers || true
            fi

            if mariadb --protocol=socket -N -e 'SELECT 1' >/dev/null 2>&1; then
              echo "> root logs in through the socket: setting the new password"
              mariadb < "$secret" || fail "Changing the root password failed (see above)."
            else
              echo "> root cannot log in through the socket: restarting MariaDB without privilege checks for a moment"
              echo "  (--skip-grant-tables --skip-networking: nobody can connect from outside meanwhile)"
              safe=$(command -v mariadbd-safe || command -v mysqld_safe) || fail "mariadbd-safe is missing, so MariaDB cannot be started without privilege checks."
              service_mariadb stop || true
              # Without systemd the init script stops MariaDB by logging in as root, which is what does not work: stop it
              # with SIGTERM instead (a normal shutdown), the mariadbd-safe wrapper first so it does not start it again.
              stopped() { ! pgrep -x mariadbd >/dev/null && ! pgrep -x mysqld >/dev/null; }
              for i in $(seq 1 10); do stopped && break; sleep 1; done
              if ! stopped; then
                echo "  stopping it with SIGTERM instead"
                # By process name: -f would match this script's own command line.
                pkill -TERM -x mariadbd-safe 2>/dev/null || pkill -TERM -x mysqld_safe 2>/dev/null || true
                pkill -TERM -x mariadbd 2>/dev/null || pkill -TERM -x mysqld 2>/dev/null || true
                for i in $(seq 1 60); do stopped && break; sleep 1; done
              fi
              stopped || fail "MariaDB does not stop, so it cannot be started without privilege checks."
              install -d -o mysql -g mysql /run/mysqld
              "$safe" --skip-grant-tables --skip-networking </dev/null >/dev/null 2>&1 &
              answers || { service_mariadb start || true; fail "MariaDB did not start with --skip-grant-tables (journalctl -u mariadb)."; }
              changed=yes
              mariadb < "$secret" || changed=
              echo "> Starting MariaDB normally again"
              mariadb-admin shutdown 2>/dev/null || mysqladmin shutdown 2>/dev/null || true
              for i in $(seq 1 60); do stopped && break; sleep 1; done
              service_mariadb start
              answers || fail "MariaDB does not answer after the restart (journalctl -u mariadb)."
              [ -n "$changed" ] || fail "Changing the root password failed (see above). MariaDB runs normally again."
            fi
            out=$(mariadb --protocol=socket -N -e 'SELECT CURRENT_USER()' 2>&1) || fail "The socket login of root does not work: $out"
            echo "  root (socket): logged in as $out"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Resetting the root password failed", token, stdin: sql);
    }

    /// <summary>
    /// The CMS and emulator accounts as AtomCMS' .env and the emulator's config.ini use them, for a new credentials file.
    /// Values that are not found are null.
    /// </summary>
    public static async Task<(string? Database, string? CmsUser, string? CmsPassword, string? EmulatorUser, string? EmulatorPassword)> ReadAccountsAsync(
        ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            env={{AtomCmsInstaller.Directory}}/.env
            ini={{EmulatorInstaller.Directory}}/config.ini
            value() { grep -sE "^$1=" "$2" | head -1 | cut -d= -f2- | sed -E 's/^"(.*)"$/\1/' | tr -d '\r'; }
            echo "DB=$(value db.database "$ini")"
            echo "CMSUSER=$(value DB_USERNAME "$env")"
            echo "CMSPASS=$(value DB_PASSWORD "$env")"
            echo "EMUUSER=$(value db.username "$ini")"
            echo "EMUPASS=$(value db.password "$ini")"
            """;
        // The passwords are only read into memory, never logged.
        ScriptValues values = await RemoteScript.RunCapturingAsync(connection, script, _ => { }, "Reading the database accounts failed",
            ["DB", "CMSUSER", "CMSPASS", "EMUUSER", "EMUPASS"], token);
        return (values["DB"], values["CMSUSER"], values["CMSPASS"], values["EMUUSER"], values["EMUPASS"]);
    }

    // The SQL goes over stdin so the passwords never appear on a command line.
    public static Task CreateUsersAsync(ServerConnection connection, DatabaseCredentials credentials, Action<string> log,
        CancellationToken token = default)
    {
        string root = Account(credentials.RootUser), cms = Account(credentials.CmsUser), db = Identifier(credentials.Database);
        string sql = $"""
            CREATE USER IF NOT EXISTS {root} IDENTIFIED BY {Sql.Literal(credentials.RootPassword)};
            ALTER USER {root} IDENTIFIED BY {Sql.Literal(credentials.RootPassword)};
            GRANT ALL PRIVILEGES ON *.* TO {root} WITH GRANT OPTION;
            {LocalRootPasswordSql(credentials)}
            CREATE USER IF NOT EXISTS {cms} IDENTIFIED BY {Sql.Literal(credentials.CmsPassword)};
            ALTER USER {cms} IDENTIFIED BY {Sql.Literal(credentials.CmsPassword)};
            CREATE SCHEMA IF NOT EXISTS {db};
            GRANT {CmsPrivileges} ON {db}.* TO {cms};
            FLUSH PRIVILEGES;

            """;
        string script = RemoteScript.Prelude + ReadSecret + $$"""

            echo "> Creating {{credentials.RootUser}}@'%', {{credentials.CmsUser}}@'%' and the database {{credentials.Database}}"
            mariadb < "$secret"
            # The password hash stays out of the log.
            mariadb -N -e "SHOW GRANTS FOR {{cms.Replace("\"", "\\\"")}}" | sed -E "s/(IDENTIFIED BY PASSWORD) '[^']*'/\1 '***'/"
            """;
        return RemoteScript.RunAsync(connection, script, log, "Creating the users failed", token, stdin: sql);
    }

    /// <summary>
    /// Returns null if <paramref name="user"/> can log in and use the database, else the reason. Connects to the
    /// server's own network address so user@'%' is tested (unless <paramref name="remote"/> is false).
    /// <paramref name="local"/> also tests 127.0.0.1 and the socket.
    /// </summary>
    public static async Task<string?> TestLoginAsync(ServerConnection connection, string user, string password, string? database,
        bool local, Action<string> log, CancellationToken token = default, bool remote = true)
    {
        string options = $"[client]\nuser={user}\npassword={password}\n";
        string check = database is null
            ? "SELECT CURRENT_USER()"
            : $"SELECT CURRENT_USER(); CREATE TABLE {Identifier(database)}.aio_install_check (id INT); DROP TABLE {Identifier(database)}.aio_install_check";
        string script = RemoteScript.Prelude + ReadSecret + $$"""
            ip=$(hostname -I 2>/dev/null | tr ' ' '\n' | grep -vE '^(127\.|::1|fe80)' | head -1)
            set --
            {{(remote ? "" : "ip=")}}
            [ -n "$ip" ] && set -- "--protocol=tcp --host=$ip"
            {{(local ? "set -- \"$@\" \"--protocol=tcp --host=127.0.0.1\" \"--protocol=socket\"" : "")}}
            [ $# -gt 0 ] || { echo "  no network address to test a remote login from"; exit 0; }
            for how in "$@"; do
              # Single quotes because the SQL has backticks, which bash would run inside double quotes.
              out=$(mariadb --defaults-extra-file="$secret" $how -N -e '{{check}}' 2>&1) || fail "Login as {{user}} ($how) failed: $out"
              echo "  {{user}} ($how): logged in as $(echo "$out" | head -1)"
            done
            """;
        try
        {
            await RemoteScript.RunAsync(connection, script, log, $"Testing {user} failed", token, stdin: options);
            return null;
        }
        catch (ServerConnectionException ex)
        {
            return ex.Message;
        }
    }

    // Local root must still log in through unix_socket without a password.
    public static async Task<string?> TestLocalRootAsync(ServerConnection connection, Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + """
            out=$(mariadb --protocol=socket -N -e 'SELECT CURRENT_USER()' 2>&1) || fail "Local root login (unix_socket) failed: $out"
            echo "  root (--protocol=socket, unix_socket): logged in as $out"
            """;
        try
        {
            await RemoteScript.RunAsync(connection, script, log, "Testing local root failed", token);
            return null;
        }
        catch (ServerConnectionException ex)
        {
            return ex.Message;
        }
    }

    private static string Account(string user) => $"{Sql.Literal(user)}@'%'";
    private static string Identifier(string name) => "`" + name.Replace("`", "``") + "`";
}
