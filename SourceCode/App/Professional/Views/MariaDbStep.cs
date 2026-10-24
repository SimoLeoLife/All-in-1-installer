using Avalonia.Controls;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Installs MariaDB 12.3, secures it, and creates root@'%', cms@'%' and the habbo database.</summary>
public sealed class MariaDbStep : InstallStep
{
    private enum UpgradeChoice { Upgrade, Abort }

    public override string Title => "MariaDB";
    public override string Caption => $"Database {MariaDbInstaller.Branch}";
    public override string InstallLabel => "Install MariaDB";

    public MariaDbStep() : base(Icons.Database,
        $"MariaDB {MariaDbInstaller.Branch}",
        "The database for Polaris and the CMS.",
        [
            $"Adds the official MariaDB {MariaDbInstaller.Branch} repository; its signing key is checked against the published fingerprint.",
            "Does what mariadb-secure-installation does: removes anonymous users and the test database, keeps root's socket login and remote root.",
            "Sets bind-address = 0.0.0.0; port 3306 stays closed in the firewall: HeidiSQL and the like connect through an SSH tunnel with your key.",
            "Creates root@'%', cms@'%' and the habbo database with strong generated passwords, and tests both logins."
        ],
        "The passwords are saved on this computer in configuration/database/<server>/mariadb.txt.",
        [
            "Check the server",
            $"Add the MariaDB {MariaDbInstaller.Branch} repository",
            $"Install MariaDB {MariaDbInstaller.Branch}",
            "Secure it and listen on 0.0.0.0",
            "Create the users and the habbo database",
            "Test the logins"
        ])
    {
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        MariaDbState state = await Task.Run(() => MariaDbInstaller.InspectAsync(connection));
        DatabaseCredentials? saved = ClientConfiguration.LoadDatabaseCredentials(connection.Login.Host);
        string path = ClientConfiguration.DatabaseCredentialsPath(connection.Login.Host);

        if (state.IsComplete && saved is not null)
        {
            string? problem = await Task.Run(() => MariaDbInstaller.TestLoginAsync(connection, saved.CmsUser, saved.CmsPassword, saved.Database, local: true, _ => { }));
            if (problem is null)
            {
                Detected.Show(ResultKind.Success, $"MariaDB {state.DisplayVersion} is already installed",
                    $"The users and the habbo database exist and the saved passwords work ({path}). You can skip this step.");
                return true;
            }
            Detected.Show(ResultKind.Warning, $"MariaDB {state.DisplayVersion} is installed, but the saved passwords do not work",
                "Installing sets the passwords from the configuration folder again.");
            return false;
        }

        if (state.MySqlInstalled)
            Detected.Show(ResultKind.Warning, "MySQL is installed", "MariaDB cannot be installed next to MySQL. Remove MySQL first.");
        else if (state.NeedsUpgrade)
            Detected.Show(ResultKind.Warning, $"MariaDB {state.DisplayVersion} is installed",
                $"Installing upgrades it to MariaDB {MariaDbInstaller.Branch}; all databases are backed up first.");
        else if (state.UsersExist && saved is null)
            Detected.Show(ResultKind.Info, "The database users exist, but their passwords are not in the configuration folder",
                "Installing gives root@'%' and cms@'%' new passwords and saves them. Anything using the old passwords must be updated.");
        else if (state.InstalledVersion is not null)
            Detected.Show(ResultKind.Info, $"MariaDB {state.DisplayVersion} is installed, but not set up completely",
                "Installing finishes the setup and keeps your data.");
        else
            Detected.Show(ResultKind.Info, "MariaDB is not installed yet", "Install it now, or skip this step.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string host = connection.Login.Host;

        Run.Log("> Checking the server ...");
        MariaDbState state = await Task.Run(() => MariaDbInstaller.InspectAsync(connection));
        Run.Log($"  {connection.Os?.PrettyName} ({state.Codename})");
        Run.Log(state.InstalledVersion is null ? "  MariaDB is not installed yet." : $"  mariadb-server {state.DisplayVersion} is installed" +
            (state.FromMariaDbOrg ? " (mariadb.org)." : " (from the distribution)."));
        if (state.MySqlInstalled)
            throw new ServerConnectionException("MySQL is installed. MariaDB cannot be installed next to it: remove MySQL first (back up your data), then try again.");

        if (state.NeedsUpgrade)
        {
            Run.Busy = false;
            UpgradeChoice choice = await ChoiceDialog.ShowAsync(owner, "MariaDB already installed",
                $"MariaDB {state.DisplayVersion} is installed",
                $"It will be upgraded to MariaDB {MariaDbInstaller.Branch}. Your databases are kept and upgraded.",
                UpgradeChoice.Abort,
                new ChoiceDialog.Option<UpgradeChoice>($"Upgrade to MariaDB {MariaDbInstaller.Branch}",
                    "All databases are first saved to /root/mariadb-backup-<date>.sql.gz.", UpgradeChoice.Upgrade, Primary: true),
                new ChoiceDialog.Option<UpgradeChoice>("Abort installation", "Changes nothing on the server and disconnects.", UpgradeChoice.Abort));
            Run.Busy = true;
            if (choice == UpgradeChoice.Abort) return Aborted(connection);
        }

        // Saved passwords, or new ones saved before anything changes on the server.
        DatabaseCredentials? credentials = ClientConfiguration.LoadDatabaseCredentials(host);
        string credentialsPath = ClientConfiguration.DatabaseCredentialsPath(host);
        if (credentials is null)
        {
            credentials = DatabaseCredentials.Generate(host);
            ClientConfiguration.SaveDatabaseCredentials(credentials);
            Run.Log($"  Generated passwords for root and cms, saved in {credentialsPath}");
            if (state.UsersExist) Run.Log("  root@'%' and cms@'%' exist already: they get these new passwords.");
        }
        else
        {
            Run.Log($"  Using the saved passwords from {credentialsPath}");
        }
        Run.Done(state.InstalledVersion is null ? "fresh install" : state.NeedsUpgrade ? $"upgrading {state.DisplayVersion}" : "updating");

        await Task.Run(() => MariaDbInstaller.AddRepositoryAsync(connection, Run.Log));
        Run.Done($"mariadb.org {MariaDbInstaller.Branch} ({state.Codename})");

        if (state.NeedsUpgrade && state.Running)
        {
            string backup = await Task.Run(() => MariaDbInstaller.BackupAsync(connection, Run.Log));
            Run.Log($"  Backup: {backup}");
        }
        string version = await Task.Run(() => MariaDbInstaller.InstallAsync(connection, Run.Log));
        Run.Log($"  Installed MariaDB {version}");
        Run.Done(version);

        await Task.Run(() => MariaDbInstaller.SecureAndListenAsync(connection, Run.Log));
        Run.Done("0.0.0.0:3306");

        DatabaseCredentials account = credentials;
        await Task.Run(() => MariaDbInstaller.CreateUsersAsync(connection, account, Run.Log));
        Run.Done($"{account.RootUser}@'%', {account.CmsUser}@'%', {account.Database}");

        // Both accounts log in over TCP and socket; cms can create tables in habbo.
        Run.Log("> Testing the logins");
        string? problem = await Task.Run(() => MariaDbInstaller.TestLoginAsync(connection, account.RootUser, account.RootPassword, null, local: false, Run.Log))
                          ?? await Task.Run(() => MariaDbInstaller.TestLocalRootAsync(connection, Run.Log))
                          ?? await Task.Run(() => MariaDbInstaller.TestLoginAsync(connection, account.CmsUser, account.CmsPassword, account.Database, local: true, Run.Log));
        if (problem is not null) throw new ServerConnectionException(problem);
        Run.Done("root and cms work");

        return await SucceededAsync($"MariaDB {version} is running",
            $"Database '{account.Database}' on {host}:{account.Port}. The passwords for {account.RootUser} and {account.CmsUser} are in {credentialsPath}.");
    }
}
