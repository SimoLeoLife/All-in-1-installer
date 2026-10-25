using Avalonia;
using Avalonia.Controls;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Firewall, Polaris' websocket whitelist and visitor IP, MariaDB over SSH tunnels, and SYN flood protection.</summary>
public sealed class HardenStep : InstallStep
{
    private readonly TextBlock _firewallNote = new() { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public override string Title => "Harden";
    public override string Caption => "Firewall & TCP";
    public override string InstallLabel => "Harden the server";
    public override string RerunLabel => "Apply again";

    public HardenStep() : base(Icons.Shield,
        "Harden the server",
        "Closes everything that does not need to be reachable from the internet.",
        [
            "Firewall (ufw): deny incoming, allow outgoing, SSH rate limited (ufw limit), HTTP/HTTPS open; with Cloudflare only for Cloudflare's IP ranges.",
            "Closes MariaDB (3306) and the emulator's ports (3000, 2096: the game runs through Traefik on /ws). HeidiSQL and the like connect through an SSH tunnel with your key.",
            "Polaris: ws.whitelist = only the hotel's address, ws.ip.header = X-Forwarded-For (the emulator is stopped while they are set).",
            "TCP stack: SYN cookies, a larger SYN backlog and fewer SYN-ACK retries in /etc/sysctl.conf."
        ],
        "The firewall starts from a clean rule set; ufw keeps the old rules as /etc/ufw/*.rules.<date>. Afterwards the installer logs in again to check SSH still works.",
        [
            "Check the server",
            "Polaris: whitelist and visitor IP",
            "MariaDB: root login through an SSH tunnel",
            "TCP stack (sysctl)",
            "Firewall (ufw)",
            "Log in again over SSH"
        ])
    {
    }

    protected override Control BuildOptions()
    {
        _firewallNote.Bind(TextBlock.ForegroundProperty, "Pro.Subtle");
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_firewallNote);
        panel.Children.Add(Text(
            "MariaDB from your computer: in HeidiSQL choose \"MariaDB or MySQL (SSH tunnel)\", SSH host = the server with your SSH key, " +
            "database host 127.0.0.1 and the passwords from configuration/database/<server>/mariadb.txt.",
            11.5, "Pro.Subtle"));
        return panel;
    }

    private static string SshPorts(HardenState state, ServerConnection connection) =>
        string.Join(", ", state.SshPorts.Append(connection.Login.Port).Distinct().Order());

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        HardenState state = await Task.Run(() => HardenInstaller.InspectAsync(connection));
        if (state.Site is null)
        {
            Detected.Show(ResultKind.Warning, "The hotel's address is not known yet",
                "Run the Web access step first: the firewall and the websocket whitelist depend on it.");
            return false;
        }
        _firewallNote.Text = state.Site.Cloudflare
            ? $"Cloudflare is used: ports 80 and 443 only accept Cloudflare's IP ranges. SSH: port {SshPorts(state, connection)} (rate limited)."
            : $"Ports 80 and 443 are open for everyone (Traefik). SSH: port {SshPorts(state, connection)} (rate limited).";

        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, "The server is hardened",
                $"ufw is active (deny incoming, 3306 closed), the TCP settings are in /etc/sysctl.conf and ws.whitelist is {state.Whitelist}. " +
                "You can skip this step, or apply it again.");
            return true;
        }
        Detected.Show(state.MariaDbOpen ? ResultKind.Warning : ResultKind.Info,
            state.MariaDbOpen ? "MariaDB (3306) is open in the firewall"
                : state.UfwActive ? "The firewall is on, but not set up by this step" : "The firewall is off",
            $"Everything below is applied for {state.Site.Url}.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        Run.Log("> Checking the server ...");
        HardenState state = await Task.Run(() => HardenInstaller.InspectAsync(connection));
        WebAccessSettings site = state.Site
            ?? throw new ServerConnectionException("The hotel's address is not known: run the Web access step first.");
        Run.Log($"  {site.Url}, Cloudflare: {(site.Cloudflare ? "yes" : "no")}, SSH port(s): {SshPorts(state, connection)}");
        Run.Done(site.Url);

        // Only the hotel's own pages may open the websocket.
        bool polaris = await Task.Run(() => HardenInstaller.ConfigurePolarisAsync(connection, site, Run.Log));
        Run.Done(polaris ? $"ws.whitelist = {HardenInstaller.WhitelistHost(site)}" : "emulator not set up yet");

        // An SSH tunnel arrives as root@localhost, so that account takes the root password too.
        DatabaseCredentials? credentials = ClientConfiguration.LoadDatabaseCredentials(connection.Login.Host);
        if (credentials is null)
        {
            Run.Log($"  No saved MariaDB passwords for {connection.Login.Host} (MariaDB step skipped?): root@localhost is left as it is.");
            Run.Done("skipped");
        }
        else
        {
            await Task.Run(() => MariaDbInstaller.AllowTunnelRootLoginAsync(connection, credentials, Run.Log));
            Run.Done("root@localhost: socket or password");
        }

        await Task.Run(() => HardenInstaller.ConfigureSysctlAsync(connection, Run.Log));
        Run.Done("SYN cookies on");

        // Firewall last: the rest needs nothing from the internet.
        await Task.Run(() => HardenInstaller.ConfigureFirewallAsync(connection, site, state.SshPorts, Run.Log));
        Run.Done(site.Cloudflare ? "80/443 Cloudflare only" : "80/443 open");

        // A fresh SSH login proves the firewall still lets SSH in. This session stays open either way.
        Run.Log($"> Logging in again to {connection.Login.Host}:{connection.Login.Port} through the firewall ...");
        try
        {
            await using ServerConnection check = await ServerConnection.ConnectAsync(connection.Login, _ => { }, _ => { });
            Run.Log("  SSH login works.");
            Run.Done("OK");
        }
        catch (ServerConnectionException ex)
        {
            Run.Log("! " + ex.Message);
            Run.Fail();
            return await SucceededWithWarningAsync("The server is hardened, but a new SSH login failed",
                $"Keep this window open and check 'ufw status' on the server ({ex.Message}). To undo: ufw disable.");
        }

        string web = site.Cloudflare ? "HTTP/HTTPS only from Cloudflare" : "HTTP/HTTPS open";
        if (!polaris)
            return await SucceededWithWarningAsync("The server is hardened",
                $"Firewall on ({web}) and TCP settings applied. The emulator is not set up yet: run this step again after the Emulator step for ws.whitelist.");
        return await SucceededAsync("The server is hardened",
            $"Firewall on ({web}, SSH rate limited, MariaDB closed: connect through an SSH tunnel), " +
            $"ws.whitelist = {HardenInstaller.WhitelistHost(site)}, SYN flood protection on.");
    }
}
