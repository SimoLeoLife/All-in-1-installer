using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Installs NGINX mainline from nginx.org with the Debian sites-available / sites-enabled layout.</summary>
public sealed class NginxStep : InstallStep
{
    private enum ExistingNginxChoice { Replace, Abort }

    public override string Title => "NGINX";
    public override string Caption => "Web server";
    public override string InstallLabel => "Install NGINX";

    public NginxStep() : base(Icons.Server,
        "NGINX mainline",
        "The web server for the hotel: it serves Octane and forwards the game connection to Polaris.",
        [
            "Adds the official nginx.org mainline repository; its signing key is checked against the published fingerprint.",
            "Installs the newest NGINX mainline and keeps it updated through apt.",
            "Adds the Debian layout from nginx-common: /etc/nginx/sites-available, sites-enabled, snippets and proxy_params.",
            "Starts NGINX at boot, opens ports 80 and 443 when the ufw firewall is on, and checks that it answers."
        ],
        "The nginx-common package itself is not installed: nginx.org's NGINX conflicts with it, so the installer creates the same folders instead.",
        [
            "Check the server",
            "Add the nginx.org mainline repository",
            "Install NGINX mainline",
            "Set up sites-available / sites-enabled",
            "Start NGINX and test it"
        ])
    {
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        NginxState state = await Task.Run(() => NginxInstaller.InspectAsync(connection));
        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, $"NGINX {state.InstalledVersion} is already installed",
                "NGINX mainline from nginx.org with sites-available / sites-enabled" + (state.Running ? ", running." : " (not running right now).") +
                " You can skip this step, or run it again to upgrade and repair.");
            return true;
        }
        if (state.HasNginxOrgNginx)
            Detected.Show(ResultKind.Info, $"NGINX {state.InstalledVersion} from nginx.org is installed",
                "The sites-available / sites-enabled layout is missing; installing adds it and keeps your configuration.");
        else if (state.HasDistroNginx)
            Detected.Show(ResultKind.Warning, $"NGINX {state.InstalledVersion} from {connection.Os?.Id} is installed",
                "Installing replaces it with NGINX mainline (a backup of /etc/nginx is made first).");
        else
            Detected.Show(ResultKind.Info, "NGINX is not installed yet", "Install it now, or skip this step.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        // Nothing is changed until the user agreed.
        Run.Log("> Checking the server ...");
        NginxState state = await Task.Run(() => NginxInstaller.InspectAsync(connection));
        Run.Log($"  {connection.Os?.PrettyName} ({state.Codename})");
        Run.Log(state.InstalledVersion is null ? "  NGINX is not installed yet." : $"  NGINX {state.InstalledVersion} is installed" +
            (state.HasDistroNginx ? $" from {connection.Os?.Id ?? "the distribution"} ({string.Join(", ", state.DistroPackages)})." : " from nginx.org."));
        if (state.Port80Process is not null) Run.Log($"  Port 80 is in use by: {state.Port80Process}");

        if (state.Port80Blocked)
            throw new ServerConnectionException(
                $"Port 80 is in use by '{state.Port80Process}'. Stop and disable it first (for example: systemctl disable --now {state.Port80Process}), then try again.");

        if (state.HasDistroNginx)
        {
            Run.Busy = false;
            ExistingNginxChoice choice = await ChoiceDialog.ShowAsync(owner, "NGINX already installed",
                "NGINX from Ubuntu/Debian is installed",
                $"NGINX {state.InstalledVersion} from the {connection.Os?.Id} archive is installed. It cannot stay next to NGINX mainline from nginx.org.",
                ExistingNginxChoice.Abort,
                new ChoiceDialog.Option<ExistingNginxChoice>("Replace it with NGINX mainline",
                    "/etc/nginx is saved to /root/nginx-backup-<date>.tar.gz, the old packages are removed and the configuration starts fresh.",
                    ExistingNginxChoice.Replace, Primary: true),
                new ChoiceDialog.Option<ExistingNginxChoice>("Abort installation",
                    "Changes nothing on the server and disconnects.", ExistingNginxChoice.Abort));
            Run.Busy = true;
            if (choice == ExistingNginxChoice.Abort) return Aborted(connection);
        }
        Run.Done(state.InstalledVersion is null ? "fresh install" : state.HasDistroNginx ? "replacing distro NGINX" : $"upgrading {state.InstalledVersion}");

        await Task.Run(() => NginxInstaller.AddRepositoryAsync(connection, Run.Log));
        Run.Done($"nginx.org mainline ({state.Codename})");

        if (state.HasDistroNginx) await Task.Run(() => NginxInstaller.RemoveDistroNginxAsync(connection, Run.Log));
        string version = await Task.Run(() => NginxInstaller.InstallPackageAsync(connection, Run.Log));
        Run.Log($"  Installed NGINX {version}");
        Run.Done(version);

        await Task.Run(() => NginxInstaller.SetUpDebianLayoutAsync(connection, Run.Log));
        Run.Done("sites-enabled included");

        await Task.Run(() => NginxInstaller.StartAndTestAsync(connection, Run.Log));
        Run.Done("HTTP 200");

        return await SucceededAsync($"NGINX {version} is running",
            $"Put site configurations in /etc/nginx/sites-available and link them in sites-enabled. Test it: http://{connection.Login.Host}/");
    }
}
