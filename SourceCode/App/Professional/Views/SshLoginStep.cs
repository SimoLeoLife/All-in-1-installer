using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Optionally turns SSH password login off, so only SSH keys get in; after testing that the key works.</summary>
public sealed class SshLoginStep : InstallStep
{
    private readonly RadioButton _keysOnly = new() { GroupName = "sshlogin", Content = "Turn password login off: only SSH keys can log in" };
    private readonly RadioButton _keep = new() { GroupName = "sshlogin", Content = "Keep password login (or allow it again)", IsChecked = true };
    private readonly CheckBox _understood = new() { Content = "I understand: without my SSH key the only way in is the console of my hosting provider (KVM)" };
    private readonly TextBox _keyPassword = new() { MinWidth = 380, PasswordChar = '•', PlaceholderText = "Password of your SSH key (empty when it has none)" };
    private readonly TextBlock _keys = Text("", 12, "Pro.Subtle");

    public override string Title => "SSH login";
    public override string Caption => "Keys only (optional)";
    public override string InstallLabel => "Apply";
    public override string RerunLabel => "Apply again";

    public SshLoginStep() : base(Icons.Key,
        "SSH login with keys only",
        "Optional: turn SSH password login off, so nobody can log in by guessing a password. You decide.",
        [
            "First logs in again as root with your SSH key, in a separate session: when that fails, nothing is changed.",
            "Writes /etc/ssh/sshd_config.d/00-all-in-1-keys-only.conf: PasswordAuthentication no, KbdInteractiveAuthentication no, PermitRootLogin prohibit-password. sshd -t checks it before sshd reloads.",
            "Logs in with the key once more afterwards; when that fails, password login is put back automatically.",
            "Choose \"Keep password login\" to leave it as it is, or to allow passwords again after turning them off."
        ],
        "This session stays connected while sshd reloads.",
        ["Check the server and your key", "Log in with the key (before)", "sshd: keys only", "Log in with the key (after)"])
    {
    }

    protected override Control BuildOptions()
    {
        _keys.TextWrapping = TextWrapping.Wrap;
        var warning = new ResultBanner();
        warning.Show(ResultKind.Warning, "Keep your SSH key safe",
            "With password login off, you can only log in with your SSH key (and its password). Lose the key and the only way in is the " +
            "console of your hosting provider (KVM / VNC / rescue mode). Keep a copy: Tools → Export encrypted settings.");
        _keysOnly.IsCheckedChanged += (_, _) => _understood.IsVisible = _keysOnly.IsChecked == true;
        _understood.IsVisible = false;

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_keep);
        panel.Children.Add(_keysOnly);
        panel.Children.Add(warning);
        panel.Children.Add(_understood);
        panel.Children.Add(Text("Password of your SSH key (to test the key login)", 12, "Pro.Muted", FontWeight.SemiBold));
        panel.Children.Add(_keyPassword);
        panel.Children.Add(_keys);
        return panel;
    }

    public override string? Validate() =>
        _keysOnly.IsChecked == true && _understood.IsChecked != true
            ? "Tick that you understand the warning: without your SSH key the only way in is the console of your hosting provider."
            : null;

    // Private keys this computer has for the server: saved by the SSH key step, the session's own, and the default from Settings.
    private static List<string> PrivateKeys(ServerConnection connection)
    {
        var keys = ClientConfiguration.SavedPrivateKeys(connection.Login.Host);
        if (connection.Login.PrivateKeys is { } own) keys.AddRange(own);
        if (InstallerSettings.Load().DefaultKeyPath is { } preset) keys.Add(preset);
        return keys.Distinct().Where(File.Exists).ToList();
    }

    private static List<AuthorizedKey> PublicKeys(IEnumerable<string> privateKeys) =>
        privateKeys.Select(path => SshKeys.TryParsePublicKey(File.ReadAllText(path + ".pub"), out AuthorizedKey key, out _) ? key : null)
            .OfType<AuthorizedKey>().ToList();

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        List<string> keys = PrivateKeys(connection);
        SshLoginState state = await Task.Run(() => SshLoginInstaller.InspectAsync(connection, PublicKeys(keys)));
        _keys.Text = keys.Count == 0
            ? "No SSH key for this server on this computer (configuration/keys): install one in the SSH key step first."
            : $"Keys on this computer for this server: {string.Join(", ", keys.Select(Path.GetFileName))}.";
        if (state.KeysOnly)
        {
            _keysOnly.IsChecked = true;
            Detected.Show(ResultKind.Success, "Password login is off: only SSH keys can log in",
                $"PermitRootLogin {state.PermitRootLogin}. You can skip this step, or choose \"Keep password login\" to allow passwords again.");
            return true;
        }
        Detected.Show(state.RootHasSavedKey ? ResultKind.Info : ResultKind.Warning,
            "SSH password login is on",
            state.RootHasSavedKey
                ? "Your SSH key is in root's authorized_keys, so it can be turned off. Or keep it, and skip this step."
                : "None of the keys on this computer is in root's authorized_keys yet: run the SSH key step first if you want keys only.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        Run.Log("> Checking the server ...");
        List<string> keys = PrivateKeys(connection);
        SshLoginState state = await Task.Run(() => SshLoginInstaller.InspectAsync(connection, PublicKeys(keys)));
        Run.Log($"  Password login: {(state.PasswordLogin ? "on" : "off")}, keyboard-interactive: {(state.KeyboardInteractive ? "on" : "off")}, PermitRootLogin {state.PermitRootLogin}");

        if (_keep.IsChecked == true)
        {
            Run.Done(state.KeysOnly ? "keys only now" : "password login on");
            Run.Done("not needed");
            await Task.Run(() => SshLoginInstaller.AllowPasswordLoginAsync(connection, Run.Log));
            Run.Done("password login as configured");
            Run.Done("not needed");
            return await SucceededAsync("SSH password login is allowed",
                state.OurFile ? "The keys-only setting of this installer is removed." : "Nothing was changed: password login stays as it was.");
        }

        if (!state.RootHasSavedKey)
            throw new ServerConnectionException("None of your SSH keys on this computer is in root's authorized_keys. Run the SSH key step first, so you can still log in afterwards.");
        Run.Log($"  Keys to test: {string.Join(", ", keys.Select(Path.GetFileName))}");
        Run.Done($"{keys.Count} key(s)");

        string password = _keyPassword.Text ?? "";
        await TestKeyLoginAsync(connection, keys, password);
        Run.Done("root with key");

        await Task.Run(() => SshLoginInstaller.DisablePasswordLoginAsync(connection, Run.Log));
        Run.Done("PasswordAuthentication no");

        try
        {
            await TestKeyLoginAsync(connection, keys, password);
        }
        catch (ServerConnectionException)
        {
            Run.Log("! The key login failed after the change: putting password login back.");
            await Task.Run(() => SshLoginInstaller.AllowPasswordLoginAsync(connection, Run.Log));
            throw;
        }
        string refused = "";
        if (!connection.Login.UsesKey && connection.Login.Password.Length > 0)
        {
            Run.Log("> A password login should now be refused ...");
            try
            {
                await using ServerConnection stillOpen = await ServerConnection.ConnectAsync(connection.Login, _ => { }, _ => { });
                Run.Log("  The password still logs in!");
                refused = " Warning: a password login still worked; check sshd on the server.";
            }
            catch (ServerConnectionException)
            {
                Run.Log("  Refused, as it should be.");
            }
        }
        Run.Done("root with key");
        string message = "Only SSH keys can log in now. Keep a copy of your key (Tools → Export encrypted settings). " +
                         $"To allow passwords again: run this step with \"Keep password login\", or delete {SshLoginInstaller.DropIn} on the server console.";
        return refused.Length > 0
            ? await SucceededWithWarningAsync("Password login is off", message + refused)
            : await SucceededAsync("Password login is off", message);
    }

    private async Task TestKeyLoginAsync(ServerConnection connection, List<string> keys, string password)
    {
        Run.Log($"> New SSH login to {connection.Login.Host}:{connection.Login.Port} as root with the key ...");
        var login = new ServerLogin(connection.Login.Host, connection.Login.Port, "root", "", keys, password);
        await using ServerConnection test = await ServerConnection.ConnectAsync(login, _ => { }, _ => { });
        Run.Log($"  Logged in as {test.LoginUser} with the key.");
    }
}
