using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Installs PHP 8.5 (at least 8.5.12) with PHP-FPM and the CMS extensions from packages.sury.org.</summary>
public sealed class PhpStep : InstallStep
{
    public override string Title => "PHP";
    public override string Caption => $"PHP {PhpInstaller.Branch}";
    public override string InstallLabel => "Install PHP";

    public PhpStep() : base(Icons.Cube,
        $"PHP {PhpInstaller.Branch} with PHP-FPM",
        $"For the hotel website. Version {PhpInstaller.MinimumVersion} or newer is required: older releases have security problems.",
        [
            "Adds Ondřej Surý's PHP repository (packages.sury.org); its keyring is checked before it is installed.",
            "Installs PHP-FPM, the CLI and the extensions: mysql, xml, xmlrpc, curl, gd, imagick, mbstring, soap, zip, intl, bcmath, plus unzip.",
            $"Refuses a PHP older than {PhpInstaller.MinimumVersion} and checks that every extension is loaded.",
            "Starts PHP-FPM at boot and, when NGINX is installed, connects them and tests a PHP page through NGINX."
        ],
        "Packages: " + string.Join(" ", PhpInstaller.Packages),
        [
            "Check the server",
            "Add the packages.sury.org PHP repository",
            $"Install PHP {PhpInstaller.Branch} and extensions",
            "Check the version and extensions",
            "Start PHP-FPM and connect it to NGINX"
        ])
    {
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        PhpState state = await Task.Run(() => PhpInstaller.InspectAsync(connection));
        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, $"PHP {state.PhpVersion} is already installed",
                "PHP-FPM is running with all extensions. You can skip this step, or run it again to update.");
            return true;
        }
        if (state.PhpVersion is not null && !state.VersionOk)
            Detected.Show(ResultKind.Warning, $"PHP {state.PhpVersion} is too old",
                $"At least {PhpInstaller.MinimumVersion} is required for security fixes. Installing updates it.");
        else if (state.PhpVersion is not null)
            Detected.Show(ResultKind.Info, $"PHP {state.PhpVersion} is installed, but not complete",
                (state.MissingPackages.Count > 0 ? $"Missing: {string.Join(", ", state.MissingPackages)}. " : "") +
                (state.FpmRunning ? "" : "PHP-FPM is not running. ") + "Installing completes it.");
        else
            Detected.Show(ResultKind.Info, $"PHP {PhpInstaller.Branch} is not installed yet", "Install it now, or skip this step.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        Run.Log("> Checking the server ...");
        PhpState state = await Task.Run(() => PhpInstaller.InspectAsync(connection));
        Run.Log($"  {connection.Os?.PrettyName} ({state.Codename})");
        Run.Log(state.PhpVersion is null ? $"  PHP {PhpInstaller.Branch} is not installed yet." : $"  PHP {state.PhpVersion} is installed.");
        if (state.OtherVersions.Count > 0)
            Run.Log($"  Other PHP versions on the server: {string.Join(", ", state.OtherVersions)} (left as they are)");
        Run.Done(state.PhpVersion is null ? "fresh install" : $"updating {state.PhpVersion}");

        await Task.Run(() => PhpInstaller.AddRepositoryAsync(connection, Run.Log));
        Run.Done($"packages.sury.org ({state.Codename})");

        // The script refuses anything older than the minimum.
        string version = await Task.Run(() => PhpInstaller.InstallPackagesAsync(connection, Run.Log));
        Run.Log($"  Installed PHP {version}");
        Run.Done(version);

        await Task.Run(() => PhpInstaller.CheckExtensionsAsync(connection, Run.Log));
        Run.Done($"≥ {PhpInstaller.MinimumVersion}, all extensions");

        bool withNginx = await Task.Run(() => PhpInstaller.StartAndConnectAsync(connection, Run.Log));
        Run.Done(withNginx ? "PHP page served by NGINX" : "running (no NGINX)");

        return await SucceededAsync($"PHP {version} is running",
            withNginx
                ? "PHP-FPM listens on /run/php/php8.5-fpm.sock. In an NGINX site: include snippets/fastcgi-php.conf; fastcgi_pass unix:/run/php/php8.5-fpm.sock;"
                : "PHP-FPM listens on /run/php/php8.5-fpm.sock. NGINX is not installed, so it was not connected.");
    }
}
