using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Configures and builds the Octane client and serves it on the hotel's address.</summary>
public sealed class OctaneStep : InstallStep
{
    public override string Title => "Octane";
    public override string Caption => "Client build";
    public override string InstallLabel => "Set up and build Octane";
    public override string RerunLabel => "Configure and rebuild";

    public OctaneStep() : base(Icons.Cube,
        "Set up the Octane client",
        "Configures Octane for the hotel's address (from the web access step), builds it and serves it with NGINX.",
        [
            "Runs yarn install in /var/www/Octane-Renderer and /var/www/Octane.",
            "Creates client-mode, hotlooks, infostand_backgrounds, news, renderer-config and ui-config .json from the .example files, with the hotel's URLs (wss:// for https, ws:// for http).",
            $"Creates the asset folders those files point to in {OctaneInstaller.Gamedata}, ready for the files from the All-in-1 download tool, and a welcome message in news/news.json.",
            "Runs yarn build and points the hotel site at /var/www/Octane/dist; /var/www/Octane/build.sh builds it again later.",
            $"Camera: photos in {HotelSite.Camera} and generated badges in c_images/album1584 and groupbadge/ (the emulator's camera and imager settings; the emulator is stopped while they are set)."
        ],
        "The game assets are not copied: put them in /var/www/gamedata yourself (with the All-in-1 download tool). Existing configuration files are kept; only their URLs are updated.",
        [
            "Check the server",
            "Octane-Renderer: yarn install",
            "Octane: yarn install",
            "Configuration files and gamedata folders",
            "yarn build",
            "Serve dist/ and test",
            "Camera and badge imager in the emulator"
        ])
    {
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        OctaneState state = await Task.Run(() => OctaneInstaller.InspectAsync(connection));
        if (state.Site is null)
        {
            Detected.Show(ResultKind.Warning, "The hotel's address is not known yet",
                "Run the Web access step first: Octane is configured with the address chosen there.");
            return false;
        }
        if (!state.OctanePresent || !state.RendererPresent)
        {
            Detected.Show(ResultKind.Warning, "Octane or Octane-Renderer is not cloned",
                $"Run the clone steps first ({OctaneInstaller.Octane} and {OctaneInstaller.Renderer}).");
            return false;
        }
        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, $"Octane is built and served on {state.Site.Url}",
                $"socket.url {state.ConfiguredSocketUrl}. You can skip this step, or configure and rebuild it (e.g. after a git pull).");
            return true;
        }
        if (state.Built && state.ConfiguredSocketUrl != state.ExpectedSocketUrl)
            Detected.Show(ResultKind.Info, "Octane is built for another address",
                $"socket.url is {state.ConfiguredSocketUrl ?? "not set"}; it becomes {state.ExpectedSocketUrl}.");
        else
            Detected.Show(ResultKind.Info, $"Octane will be set up for {state.Site.Url}",
                $"socket.url {state.ExpectedSocketUrl}, api.url {state.Site.Url}, assets {state.Site.Url}/gamedata.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        Run.Log("> Checking the server ...");
        OctaneState state = await Task.Run(() => OctaneInstaller.InspectAsync(connection));
        WebAccessSettings site = state.Site
            ?? throw new ServerConnectionException("The hotel's address is not known: run the Web access step first.");
        if (!state.OctanePresent) throw new ServerConnectionException($"{OctaneInstaller.Octane} has no package.json. Run the Octane clone step first.");
        if (!state.RendererPresent) throw new ServerConnectionException($"{OctaneInstaller.Renderer} has no package.json. Run the Renderer clone step first.");
        if (state.NodeVersion is null) throw new ServerConnectionException("Node.js is not installed in /usr/bin. Run the Node.js step first.");
        Run.Log($"  {site.Url}: socket {OctaneInstaller.SocketUrl(site)}, Node.js {state.NodeVersion}");
        Run.Done(site.Url);

        // Renderer first: Octane builds against its sources and node_modules.
        await Task.Run(() => OctaneInstaller.YarnInstallAsync(connection, OctaneInstaller.Renderer, Run.Log));
        Run.Done("done");
        await Task.Run(() => OctaneInstaller.YarnInstallAsync(connection, OctaneInstaller.Octane, Run.Log));
        Run.Done("done");

        await Task.Run(() => OctaneInstaller.ConfigureAsync(connection, site, Run.Log));
        Run.Done(OctaneInstaller.SocketUrl(site));

        await Task.Run(() => OctaneInstaller.BuildAsync(connection, Run.Log));
        Run.Done("dist/");

        await Task.Run(() => OctaneInstaller.PublishAsync(connection, site, Run.Log));
        Run.Done(site.Url);

        // The emulator writes camera photos into gamedata/camera.
        bool camera = await Task.Run(() => EmulatorInstaller.ConfigureImagerAsync(connection, site.Url, Run.Log));
        Run.Done(camera ? $"{site.Url}/gamedata/camera/" : "emulator not set up yet");

        if (!camera)
            return await SucceededWithWarningAsync($"Octane is served on {site.Url}",
                "The emulator is not set up yet, so its camera and badge settings are not set: run the Emulator step, then this step again. " +
                $"Copy the game assets into {OctaneInstaller.Gamedata} with the All-in-1 download tool.");
        return await SucceededAsync($"Octane is served on {site.Url}",
            $"Copy the game assets into {OctaneInstaller.Gamedata} (the folders are there) with the All-in-1 download tool. " +
            $"After changing {OctaneInstaller.ConfigurationDirectory}, run {OctaneInstaller.Octane}/build.sh.");
    }
}
