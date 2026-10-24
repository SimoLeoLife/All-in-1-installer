using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Docker + Traefik in front of NGINX (port 85) and Polaris (/ws and /api on port 2096).</summary>
public sealed partial class WebAccessStep : InstallStep
{
    private enum ConflictChoice { Replace, Abort }

    private readonly RadioButton _cloudflareNo = new() { GroupName = "cloudflare", Content = "No", IsChecked = true };
    private readonly RadioButton _cloudflareYes = new() { GroupName = "cloudflare", Content = "Yes, the domain runs through Cloudflare" };
    private readonly RadioButton _https = new() { GroupName = "scheme", Content = "https (Let's Encrypt certificate)", IsChecked = true };
    private readonly RadioButton _http = new() { GroupName = "scheme", Content = "http" };
    private readonly TextBox _address = new() { MinWidth = 380, PlaceholderText = "example.com  or  203.0.113.10" };
    private readonly TextBlock _preview = new() { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public override string Title => "Web access";
    public override string Caption => "Docker & Traefik";
    public override string InstallLabel => "Set up web access";
    public override string RerunLabel => "Set up again";

    public WebAccessStep() : base(Icons.Globe,
        "Web access with Traefik",
        "How visitors reach the hotel: Traefik answers on ports 80 and 443 and passes everything on.",
        [
            "Installs Docker Engine from the official Docker repository (docs.docker.com/engine/install); its signing key is checked.",
            "Runs Traefik v3 in Docker (/opt/traefik). With https it gets and renews a Let's Encrypt certificate.",
            $"NGINX now only listens on localhost:{WebAccessInstaller.NginxPort}: Traefik sends the site there.",
            $"/ws (the game websocket) and /api go straight to Polaris on localhost:{WebAccessInstaller.PolarisPort}; the address is added to the emulator's whitelist."
        ],
        "With https the domain must already point to this server (or to Cloudflare), and port 80 must be reachable from the internet for Let's Encrypt.",
        [
            "Check the server",
            "Install Docker Engine",
            $"NGINX site on localhost:{WebAccessInstaller.NginxPort}",
            "Start Traefik",
            "Polaris: whitelist and visitor IP",
            "Test the site, /api and /ws"
        ])
    {
    }

    protected override Control BuildOptions()
    {
        static Control Question(string label, params Control[] inputs)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Text(label, 12, "Pro.Muted", Avalonia.Media.FontWeight.SemiBold));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            foreach (Control input in inputs) row.Children.Add(input);
            panel.Children.Add(row);
            return panel;
        }

        _preview.Bind(TextBlock.ForegroundProperty, "Pro.Subtle");
        _address.TextChanged += (_, _) => UpdatePreview();
        foreach (RadioButton radio in new[] { _cloudflareNo, _cloudflareYes, _https, _http })
            radio.IsCheckedChanged += (_, _) => UpdatePreview();

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Question("Do you use Cloudflare?", _cloudflareNo, _cloudflareYes));
        panel.Children.Add(Question("http or https?", _https, _http));
        panel.Children.Add(Question("The hotel's address (domain name or IP)", _address));
        panel.Children.Add(_preview);
        UpdatePreview();
        return panel;
    }

    /// <summary>"https://example.com/" typed in the address box: take the scheme from it and keep the host.</summary>
    private WebAccessSettings Settings()
    {
        string text = (_address.Text ?? "").Trim();
        bool https = _https.IsChecked == true;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { https = true; text = text[8..]; }
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { https = false; text = text[7..]; }
        text = text.Split('/')[0].Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
        return new WebAccessSettings(text, https, _cloudflareYes.IsChecked == true);
    }

    private void UpdatePreview()
    {
        WebAccessSettings s = Settings();
        if (s.Address.Length == 0) { _preview.Text = ""; return; }
        _preview.Text = $"{s.Url}  →  NGINX on localhost:{WebAccessInstaller.NginxPort}\n" +
                        $"{s.Url}/ws  and  {s.Url}/api  →  Polaris on localhost:{WebAccessInstaller.PolarisPort}" +
                        (s.Cloudflare ? "\nCloudflare: set SSL/TLS to \"Full (strict)\" for https, and keep the DNS record proxied (orange cloud)." : "");
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainName();

    public override string? Validate()
    {
        string raw = (_address.Text ?? "").Trim();
        if (raw.Length == 0) return "Enter the hotel's address: a domain name like example.com, or the server's IP address.";
        WebAccessSettings s = Settings();
        if (s.Address.Contains(':') && !s.IsIpAddress) return "Leave out the port: Traefik answers on 80 (http) and 443 (https).";
        if (!s.IsIpAddress && !DomainName().IsMatch(s.Address)) return $"'{s.Address}' is not a valid domain name or IP address.";
        if (s.IsIpAddress && s.Cloudflare) return "Cloudflare works with a domain name, not an IP address.";
        if (s.IsIpAddress && s.Https) return "Let's Encrypt only gives certificates for domain names. Use http with an IP address, or enter your domain.";
        // Keep the radio buttons in line with a scheme typed in the address box.
        (s.Https ? _https : _http).IsChecked = true;
        _address.Text = s.Address;
        return null;
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        WebAccessState state = await Task.Run(() => WebAccessInstaller.InspectAsync(connection));
        if (state.Configured is { } configured && string.IsNullOrWhiteSpace(_address.Text))
        {
            _address.Text = configured.Address;
            (configured.Https ? _https : _http).IsChecked = true;
            (configured.Cloudflare ? _cloudflareYes : _cloudflareNo).IsChecked = true;
        }

        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, $"Traefik is running for {state.Configured!.Url}",
                $"Docker {state.DockerVersion}, Traefik in {WebAccessInstaller.TraefikDirectory}, NGINX on localhost:{WebAccessInstaller.NginxPort}. " +
                "You can skip this step, or set it up again (for example with another address).");
            return true;
        }
        if (state.ConflictingPackages.Count > 0)
            Detected.Show(ResultKind.Warning, "Docker from the distribution is installed",
                $"{string.Join(", ", state.ConflictingPackages)} conflict with Docker Engine; the step asks before it replaces them.");
        else if (state.DockerVersion is not null)
            Detected.Show(ResultKind.Info, $"Docker {state.DockerVersion} is installed", "Answer the questions below to set up Traefik.");
        else
            Detected.Show(ResultKind.Info, "Docker and Traefik are not installed yet", "Answer the questions below, then set up web access.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        WebAccessSettings settings = Settings();

        // Traefik takes ports 80/443; NGINX moves to 85.
        Run.Log("> Checking the server ...");
        WebAccessState state = await Task.Run(() => WebAccessInstaller.InspectAsync(connection));
        // The AtomCMS addon stays in place (CMS on the site, Octane on /client).
        settings = settings with { AtomCms = state.Configured?.AtomCms == true };
        Run.Log($"  {settings.Url}, Cloudflare: {(settings.Cloudflare ? "yes" : "no")}");
        Run.Log(state.DockerVersion is null ? "  Docker Engine is not installed yet." : $"  Docker {state.DockerVersion} is installed.");
        foreach ((string port, string? owner80) in new[] { ("80", state.Port80Process), ("443", state.Port443Process) })
        {
            if (owner80 is null) continue;
            Run.Log($"  Port {port} is in use by: {owner80}");
            if (owner80 is not ("nginx" or "traefik" or "docker-proxy"))
                throw new ServerConnectionException(
                    $"Port {port} is in use by '{owner80}', and Traefik needs it. Stop and disable it first (for example: systemctl disable --now {owner80}), then try again.");
        }
        if (!state.EmulatorRunning)
            Run.Log($"  Nothing listens on port {WebAccessInstaller.PolarisPort} yet: /ws and /api only answer once the emulator runs.");

        if (state.ConflictingPackages.Count > 0)
        {
            Run.Busy = false;
            ConflictChoice choice = await ChoiceDialog.ShowAsync(owner, "Docker already installed",
                "Docker from Ubuntu/Debian is installed",
                $"{string.Join(", ", state.ConflictingPackages)} conflict with Docker Engine from docker.com (docs.docker.com: \"Uninstall old versions\").",
                ConflictChoice.Abort,
                new ChoiceDialog.Option<ConflictChoice>("Replace it with Docker Engine",
                    "Removes those packages and installs Docker Engine. Images, containers and volumes in /var/lib/docker are kept.",
                    ConflictChoice.Replace, Primary: true),
                new ChoiceDialog.Option<ConflictChoice>("Abort installation",
                    "Changes nothing on the server and disconnects.", ConflictChoice.Abort));
            Run.Busy = true;
            if (choice == ConflictChoice.Abort) return Aborted(connection);
        }
        Run.Done(settings.Url);

        // Also on a re-run, so apt upgrades Docker.
        await Task.Run(() => WebAccessInstaller.InstallDockerAsync(connection, Run.Log));
        Run.Done("docker-ce, compose plugin");

        await Task.Run(() => WebAccessInstaller.ConfigureNginxAsync(connection, settings, Run.Log));
        Run.Done($"127.0.0.1:{WebAccessInstaller.NginxPort}");

        await Task.Run(() => WebAccessInstaller.ConfigureTraefikAsync(connection, settings, Run.Log));
        Run.Done(settings.Https ? "ports 80 + 443" : "port 80");

        Run.Log("> Polaris: ws.whitelist and ws.ip.header (emulator_settings)");
        bool polaris = await Task.Run(() => WebAccessInstaller.ConfigurePolarisAsync(connection, settings, Run.Log));
        Run.Done(polaris ? $"{settings.Address} whitelisted" : "emulator not set up yet");

        // Test through Traefik from the server itself.
        WebAccessResult result = await Task.Run(() => WebAccessInstaller.TestAsync(connection, settings, Run.Log));
        Run.Done(result.WebSocketAnswered ? "site, /api and /ws answer" : "site answers");

        string routes = $"{settings.Url} → NGINX (localhost:{WebAccessInstaller.NginxPort}); {settings.Url}/ws and /api → Polaris (localhost:{WebAccessInstaller.PolarisPort}).";
        var problems = new List<string>();
        if (settings.Https && !result.CertificateValid)
            problems.Add($"There is no Let's Encrypt certificate yet ({result.CertificateProblem}). Check that {settings.Address} points to " +
                         (settings.Cloudflare ? "Cloudflare (proxied)" : "this server") + " and that port 80 is open; Traefik keeps trying.");
        if (!result.ApiAnswered || !result.WebSocketAnswered)
            problems.Add(state.EmulatorRunning || polaris
                ? "Polaris did not answer on /api or /ws: check that the emulator runs (Emulator step) and see /var/log/emu/emulator.log."
                : "Polaris does not run yet: run the Emulator step, then this step again.");
        if (problems.Count > 0)
            return await SucceededWithWarningAsync("Traefik is running, with something to check", routes + " " + string.Join(" ", problems));
        return await SucceededAsync($"The hotel answers on {settings.Url}",
            routes + (settings.Https ? " The certificate renews automatically." : "") + $" Settings: {WebAccessInstaller.TraefikDirectory}.");
    }
}
