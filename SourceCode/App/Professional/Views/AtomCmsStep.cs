using Avalonia;
using Avalonia.Controls;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>AtomCMS becomes the hotel's website; Octane moves to /client/.</summary>
public sealed class AtomCmsStep : InstallStep
{
    private enum ExistingFolderChoice { MoveAside, Abort }

    private readonly TextBox _hotelName = new() { MinWidth = 380, MaxLength = 100, PlaceholderText = "My Hotel" };

    /// <summary>Set after a run: the key AtomCMS' web installer asks for.</summary>
    public string? InstallationKey { get; private set; }
    public bool InstallationCompleted { get; private set; }

    public override string Title => "AtomCMS";
    public override string Caption => "CMS & /client";
    public override string InstallLabel => "Install AtomCMS";
    public override string RerunLabel => "Update and rebuild";

    public AtomCmsStep() : base(Icons.Puzzle,
        "Install AtomCMS",
        "AtomCMS (github.com/ObjectRetros/atomcms) becomes the hotel's website: visitors register and log in there, and the CMS opens Octane on /client.",
        [
            $"Clones AtomCMS to {AtomCmsInstaller.Directory}, writes .env (address, database login cms, https) and runs composer install.",
            "php artisan key:generate, storage:link and migrate --seed: AtomCMS' tables go into the hotel database next to the emulator's.",
            "Sets the CMS' settings for this server: theme atom, nitro_path /client, the badge, group badge and furniture icon paths in /gamedata.",
            "Builds the atom theme and the housekeeping (npm ci, npm run build:atom).",
            "Octane moves to /client: vite --base=/client/ in package.json, client-mode.json with /client/configuration/, its login screen off (the CMS logs in), and a rebuild.",
            $"NGINX serves {AtomCmsInstaller.Public} with PHP 8.5 and Octane on /client/; Traefik sends only Polaris' own /api paths to the emulator, AtomCMS keeps the rest of /api."
        ],
        "Run the full server installation first (at least NGINX, PHP, MariaDB, Dev tools, the clones, Emulator, Web access and Octane), from this computer: the database login comes from configuration/database/<server>/mariadb.txt.",
        [
            "Check the server",
            "Clone or update AtomCMS",
            ".env",
            "composer install",
            "Database: migrate and settings",
            "Theme: npm run build:atom",
            "Octane on /client: configure and build",
            "NGINX and Traefik",
            "Test the CMS, /client and /api",
            "Installation key for the web installer"
        ])
    {
    }

    protected override Control BuildOptions()
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Text("The hotel's name (APP_NAME and the CMS' hotel_name)", 12, "Pro.Muted", Avalonia.Media.FontWeight.SemiBold));
        panel.Children.Add(_hotelName);
        return panel;
    }

    public override string? Validate()
    {
        string name = (_hotelName.Text ?? "").Trim();
        if (name.Length == 0) return "Enter the hotel's name.";
        if (name.Any(char.IsControl)) return "The hotel's name cannot contain line breaks or tabs.";
        _hotelName.Text = name;
        return null;
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        AtomCmsState state = await Task.Run(() => AtomCmsInstaller.InspectAsync(connection));
        if (string.IsNullOrWhiteSpace(_hotelName.Text))
            _hotelName.Text = state.HotelName is { Length: > 0 } name && name != "Atom CMS" ? name
                : state.Site is { IsIpAddress: false } site && HardenInstaller.WhitelistHost(site).Split('.')[0] is { Length: > 0 } host
                    ? char.ToUpperInvariant(host[0]) + host[1..] + " Hotel" : "";

        if (Missing(state) is { } missing)
        {
            Detected.Show(ResultKind.Warning, "The server is not ready for AtomCMS yet", missing);
            return false;
        }
        if (state.IsComplete)
        {
            InstallationKey = state.InstallationKey;
            InstallationCompleted = state.InstallationCompleted;
            Detected.Show(ResultKind.Success, $"AtomCMS is the website of {state.Site!.Url}",
                $"Octane is on {state.Site.ClientUrl}. " +
                (state.InstallationCompleted ? "The AtomCMS web installer is done. "
                    : state.InstallationKey is null ? "The installation key for AtomCMS' web installer is made when this step runs. "
                    : $"Finish AtomCMS' web installer on {state.Site.Url} with key {state.InstallationKey}. ") +
                "You can skip this step, or update AtomCMS (git pull, composer, migrations) and rebuild.");
            return true;
        }
        Detected.Show(ResultKind.Info, $"AtomCMS will be installed for {state.Site!.Url}",
            $"The CMS on {state.Site.Url}, Octane on {state.Site.Url}/client/ (PHP {state.PhpVersion}, Node.js {state.NodeVersion}).");
        return false;
    }

    private static string? Missing(AtomCmsState state) =>
        state.Site is null ? "Run the Web access step first: AtomCMS is set up for the address chosen there."
        : state.PhpVersion is null ? "PHP 8.5 is not installed: run the PHP step first."
        : !state.ComposerPresent || state.NodeVersion is null ? "Composer or Node.js is missing: run the Dev tools step first."
        : !state.EmulatorConfigured ? "The emulator is not set up: run the Emulator step first (AtomCMS uses its database)."
        : !state.OctaneBuilt ? "Octane is not built yet: run the Octane step first."
        : null;

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string hotelName = (_hotelName.Text ?? "").Trim();
        Run.Log("> Checking the server ...");
        AtomCmsState state = await Task.Run(() => AtomCmsInstaller.InspectAsync(connection));
        if (Missing(state) is { } missing) throw new ServerConnectionException(missing);
        WebAccessSettings site = state.Site! with { AtomCms = true };
        Run.Log($"  {site.Url}: PHP {state.PhpVersion}, Node.js {state.NodeVersion}, Octane built");
        Run.Done(site.Url);

        CloneState clone = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, AtomCmsInstaller.Repository, AtomCmsInstaller.Directory));
        if (clone.Kind == CloneTargetKind.OtherContent)
        {
            Run.Busy = false;
            ExistingFolderChoice choice = await ChoiceDialog.ShowAsync(owner, "Folder not empty",
                $"{AtomCmsInstaller.Directory} already has other content",
                clone.Origin is null ? "It is not a clone of AtomCMS." : $"It is a clone of {clone.Origin}, not of ObjectRetros/atomcms.",
                ExistingFolderChoice.Abort,
                new ChoiceDialog.Option<ExistingFolderChoice>("Move it aside and clone",
                    $"{AtomCmsInstaller.Directory} is renamed to {AtomCmsInstaller.Directory}.bak-<date>; nothing is deleted.", ExistingFolderChoice.MoveAside, Primary: true),
                new ChoiceDialog.Option<ExistingFolderChoice>("Abort installation", "Changes nothing on the server and disconnects.",
                    ExistingFolderChoice.Abort));
            Run.Busy = true;
            if (choice == ExistingFolderChoice.Abort) return Aborted(connection);
            await Task.Run(() => GitCloneInstaller.MoveAsideAsync(connection, AtomCmsInstaller.Directory, Run.Log));
        }
        string commit = clone.Kind == CloneTargetKind.SameRepository
            ? await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, AtomCmsInstaller.Directory, null, Run.Log))
            : await Task.Run(() => GitCloneInstaller.CloneAsync(connection, AtomCmsInstaller.Repository, AtomCmsInstaller.Directory, null, Run.Log));
        Run.Done(commit);

        DatabaseCredentials? credentials = ClientConfiguration.LoadDatabaseCredentials(connection.Login.Host);
        if (credentials is null) Run.Log($"  No saved MariaDB passwords for {connection.Login.Host}: the database login already in .env is kept.");
        await Task.Run(() => AtomCmsInstaller.ConfigureEnvAsync(connection, site, hotelName, credentials, Run.Log));
        Run.Done(site.Url);

        await Task.Run(() => AtomCmsInstaller.ComposerInstallAsync(connection, Run.Log));
        Run.Done("vendor/");

        bool first = await Task.Run(() => AtomCmsInstaller.MigrateAsync(connection, hotelName, Run.Log));
        Run.Done(first ? "installed and seeded" : "up to date");

        await Task.Run(() => AtomCmsInstaller.BuildThemeAsync(connection, Run.Log));
        Run.Done("public/build");

        // Octane is configured and built for /client/ before the site switches, so it is never served half-way.
        await Task.Run(() => OctaneInstaller.ConfigureAsync(connection, site, Run.Log));
        await Task.Run(() => OctaneInstaller.BuildAsync(connection, site, Run.Log));
        Run.Done(site.ClientUrl);

        await Task.Run(() => WebAccessInstaller.ConfigureRoutesAsync(connection, site, Run.Log));
        await Task.Run(() => OctaneInstaller.PublishAsync(connection, site, Run.Log));
        Run.Done("CMS on /, Octane on /client/");

        await Task.Run(() => AtomCmsInstaller.TestAsync(connection, site, Run.Log));
        Run.Done("OK");

        (InstallationKey, InstallationCompleted) = await Task.Run(() => AtomCmsInstaller.InstallationKeyAsync(connection, site, Run.Log));
        Run.Done(InstallationCompleted ? "web installer done" : InstallationKey!);
        if (InstallationCompleted)
            return await SucceededAsync($"AtomCMS is the website of {site.Url}",
                $"Octane is on {site.ClientUrl}; the CMS opens it after logging in. Update later by running this step again.");
        Run.Extra.Content = FinishedView.CopyBox(InstallationKey!);
        return await SucceededWithWarningAsync($"AtomCMS is installed on {site.Url}",
            $"Copy the installation key below, open {site.Url} and paste it in AtomCMS' web installer. The paths are already filled in.");
    }
}
