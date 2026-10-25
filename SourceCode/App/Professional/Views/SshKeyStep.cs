using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

public enum KeyStepOutcome { Installed, Failed, Aborted }

/// <summary>
/// Puts an SSH key in /root/.ssh/authorized_keys: a key pair generated here (saved in the configuration
/// directory) or a public key the user provides. Asks first when keys are already installed.
/// </summary>
public sealed class SshKeyStep
{

    private readonly RadioButton _generate = new() { GroupName = "keysource", IsChecked = true };
    private readonly RadioButton _provide = new() { GroupName = "keysource" };
    private readonly ComboBox _keyType = new()
    {
        MinWidth = 260,
        ItemsSource = new[] { "Ed25519 (recommended)", "RSA 4096 (older clients)" },
        SelectedIndex = 0
    };
    private readonly TextBox _comment = new() { MinWidth = 320 };
    private readonly TextBox _passphrase = new() { MinWidth = 260, PasswordChar = '•', PlaceholderText = "At least 8 characters" };
    private readonly TextBox _passphraseRepeat = new() { MinWidth = 260, PasswordChar = '•', PlaceholderText = "Type it again" };
    private const int MinimumPassphraseLength = 8;
    private readonly TextBox _publicKey = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 96,
        FontSize = 12,
        PlaceholderText = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAA... you@computer"
    };
    private readonly Button _browse = new() { Content = "Load .pub file...", Padding = new Thickness(14, 7), Classes = { "secondary" } };
    private readonly StackPanel _generateOptions = new() { Spacing = 10, Margin = new Thickness(30, 4, 0, 0) };
    private readonly StackPanel _provideOptions = new() { Spacing = 8, Margin = new Thickness(30, 4, 0, 0) };

    private readonly Control _options;
    private readonly Control _run;
    private readonly ContentControl _host = new();
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, Foreground = Solid(Brand) };
    private readonly CheckList _checks = new("Prepare the key", "Check installed keys", "Install the key", "Test key login as root");
    private readonly ResultBanner _result = new();
    private readonly ResultBanner _detected = new();
    private readonly AvaloniaList<string> _logLines = [];
    private readonly LogView _log;

    public Control View => _host;
    public bool ShowingRun => _host.Content == _run;
    public event Action<string>? Status;

    public SshKeyStep()
    {
        _publicKey.FontFamily = Mono;
        _log = new LogView(_logLines) { Height = 200 };
        _generate.Content = OptionLabel("Generate a new key pair", "Creates the keys here and stores them in the configuration directory (OpenSSH and PuTTY .ppk).");
        _provide.Content = OptionLabel("Use my own public key", "Paste your public key (id_ed25519.pub, id_rsa.pub ...) or load the file.");
        _generate.IsCheckedChanged += (_, _) => RefreshOptions();
        _browse.Click += async (_, _) => await BrowseAsync();

        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        typeRow.Children.Add(Labelled("Key type", _keyType));
        typeRow.Children.Add(Labelled("Comment", _comment));
        _generateOptions.Children.Add(typeRow);
        var passphraseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        passphraseRow.Children.Add(Labelled("Key password (passphrase)", _passphrase));
        passphraseRow.Children.Add(Labelled("Repeat password", _passphraseRepeat));
        _generateOptions.Children.Add(passphraseRow);
        _generateOptions.Children.Add(new TextBlock
        {
            Text = "The private key is encrypted with this password. You need it every time you use the key; it is not stored anywhere, so keep it safe.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left
        }.Bind(TextBlock.ForegroundProperty, "Pro.Muted"));

        _provideOptions.Children.Add(_publicKey);
        var browseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        browseRow.Children.Add(_browse);
        browseRow.Children.Add(new TextBlock { Text = "Never paste a private key: only the public key goes to the server.", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.ForegroundProperty, "Pro.Muted"));
        _provideOptions.Children.Add(browseRow);

        _options = BuildOptions();
        _run = BuildRun();
        _host.Content = _options;
        RefreshOptions();
    }

    private static Control OptionLabel(string title, string caption)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(4, 0, 0, 0) };
        panel.Children.Add(Text(title, 14, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(caption, 12.5, "Pro.Muted"));
        return panel;
    }

    private static Control Labelled(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(Text(label, 12, "Pro.Muted", FontWeight.SemiBold));
        panel.Children.Add(input);
        return panel;
    }

    private Control BuildOptions()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18 };
        var icon = Icon(Icons.Key, 22);
        icon.Foreground = Solid(Brand);
        grid.Children.Add(new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Solid(Color.FromArgb(0x26, Brand.R, Brand.G, Brand.B)),
            Child = icon
        });

        var panel = new StackPanel { Spacing = 12, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Text("SSH key for root", 19, weight: FontWeight.SemiBold));
        panel.Children.Add(Text($"The key is placed in {RootAuthorizedKeys.FilePath}, so you (and the installer) can log in as root without a password.", 13, "Pro.Muted"));
        panel.Children.Add(_detected);
        panel.Children.Add(_generate);
        panel.Children.Add(_generateOptions);
        panel.Children.Add(_provide);
        panel.Children.Add(_provideOptions);

        var folder = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        folder.Children.Add(Icon(Icons.Folder, 16).Bind(PathIcon.ForegroundProperty, "Pro.Muted"));
        var path = Text($"Keys are stored in {ClientConfiguration.Directory}", 12, "Pro.Muted");
        path.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(path, 1);
        folder.Children.Add(path);
        var open = new Button { Content = "Open folder", Padding = new Thickness(12, 5), FontSize = 12, Classes = { "secondary" } };
        open.Click += (_, _) => OpenFolder(ClientConfiguration.Directory);
        Grid.SetColumn(open, 2);
        folder.Children.Add(open);
        panel.Children.Add(folder);

        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    private Control BuildRun()
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(_progress);
        panel.Children.Add(_checks);
        panel.Children.Add(_result);
        panel.Children.Add(_log);
        return panel;
    }

    private void RefreshOptions()
    {
        bool generate = _generate.IsChecked == true;
        _generateOptions.IsVisible = generate;
        _provideOptions.IsVisible = !generate;
    }

    /// <summary>Back to the choice between generating a key and providing one.</summary>
    public void ShowOptions(string host)
    {
        if (string.IsNullOrWhiteSpace(_comment.Text)) _comment.Text = $"all-in-1-installer@{host}";
        _host.Content = _options;
    }

    /// <summary>
    /// Shows what the server has installed. True when a key this installer saved for the host
    /// (configuration/keys/&lt;host&gt;) is in authorized_keys, so the step can be skipped.
    /// </summary>
    public async Task<bool> DetectAsync(ServerConnection connection)
    {
        string host = connection.Login.Host;
        try
        {
            List<AuthorizedKey> installed = await Task.Run(() => RootAuthorizedKeys.ReadAsync(connection));
            var saved = ClientConfiguration.SavedPublicKeys(host);
            var ours = saved.Where(s => installed.Any(k => k.SameKey(s.Key))).ToList();
            if (ours.Count > 0)
            {
                (string path, AuthorizedKey key) = ours[0];
                _detected.Show(ResultKind.Success, "An SSH key is already installed",
                    $"{Path.GetFileName(path)} ({key.Fingerprint}) from the configuration folder is in {RootAuthorizedKeys.FilePath}" +
                    (installed.Count > 1 ? $", together with {installed.Count - 1} other key(s)." : ".") +
                    " You can skip this step.");
                return true;
            }
            if (installed.Count > 0)
                _detected.Show(ResultKind.Info, $"{installed.Count} key(s) already installed",
                    $"{RootAuthorizedKeys.FilePath} has keys, but none from this installer's configuration folder. Skip if one of them is yours, or install a key.");
            else
                _detected.Show(ResultKind.Info, "No SSH key installed yet",
                    "Root has no keys on this server. Install one now, or skip and keep using the password.");
            return false;
        }
        catch (Exception ex) when (ex is ServerConnectionException or IOException or UnauthorizedAccessException)
        {
            _detected.Show(ResultKind.Warning, "Could not check the installed keys", ex.Message);
            return false;
        }
    }

    public string? Validate()
    {
        if (_generate.IsChecked == true)
        {
            string passphrase = _passphrase.Text ?? "";
            if (passphrase.Length == 0) return "Set a password for the new key.";
            if (passphrase.Length < MinimumPassphraseLength) return $"The key password must be at least {MinimumPassphraseLength} characters.";
            if (passphrase != (_passphraseRepeat.Text ?? "")) return "The two key passwords are not the same.";
            return null;
        }
        return SshKeys.TryParsePublicKey(_publicKey.Text ?? "", out _, out string error) ? null : error;
    }

    public async Task<KeyStepOutcome> RunAsync(ServerConnection connection, Window owner)
    {
        _host.Content = _run;
        _logLines.Clear();
        _log.ResetFollow();
        _result.Hide();
        _checks.Reset();
        _progress.IsVisible = true;
        int stage = 0;
        string host = connection.Login.Host;

        void Log(string line)
        {
            _logLines.Add(line);
            _log.FollowOutput();
        }
        void Done(string detail)
        {
            _checks.SetDetail(stage, detail);
            _checks.Update(++stage, failed: false);
        }

        try
        {
            AuthorizedKey key;
            SshKeyPair? pair = null;
            string? savedPrivateKey = null;
            string passphrase = "";
            if (_generate.IsChecked == true)
            {
                SshKeyType type = _keyType.SelectedIndex == 1 ? SshKeyType.Rsa4096 : SshKeyType.Ed25519;
                Log($"> Generating a new {(type == SshKeyType.Ed25519 ? "Ed25519" : "RSA 4096")} key pair ...");
                string comment = _comment.Text ?? "";
                passphrase = _passphrase.Text ?? "";
                pair = await Task.Run(() => SshKeys.Generate(type, comment, passphrase));
                key = pair.PublicKey;
                // Saved before the server is touched, so a key on the server always has its private key here.
                string path = savedPrivateKey = ClientConfiguration.SaveKeyPair(host, pair);
                Log($"  Private key: {path} (encrypted with your key password)");
                Log($"  Public key:  {path}.pub");
                Log($"  PuTTY key:   {path}.ppk (same key and password, for HeidiSQL/plink, Pageant and WinSCP)");
            }
            else
            {
                SshKeys.TryParsePublicKey(_publicKey.Text ?? "", out key, out _);
                string path = ClientConfiguration.SavePublicKey(host, key);
                Log($"> Using your {key.Type} public key. Copy saved as {path}");
            }
            Log($"  Fingerprint: {key.Fingerprint}");
            Done(key.Fingerprint);

            Log($"> Reading {RootAuthorizedKeys.FilePath} ...");
            List<AuthorizedKey> existing = await Task.Run(() => RootAuthorizedKeys.ReadAsync(connection));
            Log(existing.Count == 0 ? "  No keys installed yet." : $"  {existing.Count} key(s) already installed:");
            foreach (AuthorizedKey k in existing)
                Log($"    {k.Type} {k.Fingerprint} {k.Comment}");

            AuthorizedKeysMode mode = AuthorizedKeysMode.Add;
            if (existing.Count > 0)
            {
                _progress.IsVisible = false;
                ExistingKeysChoice choice = await ExistingKeysDialog.ShowAsync(owner, existing, key);
                _progress.IsVisible = true;
                if (choice == ExistingKeysChoice.Abort)
                {
                    Log("! Installation aborted by the user. Nothing was changed on the server.");
                    if (savedPrivateKey is not null)
                    {
                        // The new key never reached the server; do not leave an unused key pair behind.
                        File.Delete(savedPrivateKey);
                        File.Delete(savedPrivateKey + ".pub");
                        File.Delete(savedPrivateKey + ".ppk");
                        Log("  The unused key pair was removed from the configuration directory.");
                    }
                    _checks.Update(stage, failed: true);
                    _result.Show(ResultKind.Warning, "Installation aborted", $"Nothing was changed on {host}.");
                    Status?.Invoke("Installation aborted.");
                    return KeyStepOutcome.Aborted;
                }
                mode = choice == ExistingKeysChoice.Replace ? AuthorizedKeysMode.Replace : AuthorizedKeysMode.Add;
                Log(mode == AuthorizedKeysMode.Replace ? "  Chosen: remove all keys and place the new one." : "  Chosen: add the new key to the existing ones.");
            }
            Done(existing.Count == 0 ? "none yet" : $"{existing.Count} found");

            Log($"> Installing the key in {RootAuthorizedKeys.FilePath} ...");
            string? backup = await Task.Run(() => RootAuthorizedKeys.InstallAsync(connection, key, mode));
            if (backup is not null) Log($"  Backup of the old file: {backup}");
            List<AuthorizedKey> now = await Task.Run(() => RootAuthorizedKeys.ReadAsync(connection));
            if (!now.Any(k => k.SameKey(key)))
                throw new ServerConnectionException("The key is not in authorized_keys after writing it.");
            Log($"  Done. {now.Count} key(s) installed now.");
            Done(mode == AuthorizedKeysMode.Replace ? "replaced all keys" : $"{now.Count} key(s) installed");

            // Key login can only be tested when the private key was generated here.
            if (pair is null)
            {
                Log("> Key login test skipped: the private key stays on your computer.");
                Done("skipped (your own key)");
                Finish(ResultKind.Success, "SSH key installed",
                    $"Your key can now log in as root on {host}. Test it with: ssh {(connection.Login.Port == 22 ? "" : $"-p {connection.Login.Port} ")}root@{host}");
                return KeyStepOutcome.Installed;
            }

            Log("> Testing key login as root ...");
            string? problem = await Task.Run(() => RootAuthorizedKeys.TestKeyLoginAsync(connection, pair.PrivateKeyPem, passphrase));
            if (problem is null)
            {
                Log("  Logged in as root with the new key.");
                Done("works");
                Finish(ResultKind.Success, "SSH key installed and tested",
                    $"The new key logs in as root on {host}. It is stored in {ClientConfiguration.KeysDirectory(host)}.");
                return KeyStepOutcome.Installed;
            }

            string? permit = await Task.Run(() => RootAuthorizedKeys.PermitRootLoginAsync(connection));
            string hint = permit == "no"
                ? "The server's sshd has 'PermitRootLogin no', so root cannot log in over SSH at all."
                : "Check the sshd settings (PubkeyAuthentication, AuthorizedKeysFile) on the server.";
            Log($"! Key login failed: {problem}. {hint}");
            _checks.Update(stage, failed: true);
            Finish(ResultKind.Warning, "Key installed, but key login did not work", $"{problem}. {hint}");
            return KeyStepOutcome.Installed;
        }
        catch (Exception ex)
        {
            string message = ex is ServerConnectionException or IOException or UnauthorizedAccessException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}";
            Log("! " + message);
            _checks.Update(stage, failed: true);
            Finish(ResultKind.Error, "Installing the SSH key failed", message);
            return KeyStepOutcome.Failed;
        }
        finally
        {
            _progress.IsVisible = false;
            // The key password is only needed for this run; do not keep it in the form.
            _passphrase.Text = "";
            _passphraseRepeat.Text = "";
        }

        void Finish(ResultKind kind, string heading, string message)
        {
            _result.Show(kind, heading, message);
            Status?.Invoke(heading + ".");
        }
    }

    private async Task BrowseAsync()
    {
        TopLevel? top = TopLevel.GetTopLevel(_host);
        if (top is null) return;
        var options = new FilePickerOpenOptions
        {
            Title = "Select your public key (.pub)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Public keys") { Patterns = ["*.pub"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        };
        string sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshFolder))
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(sshFolder);

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0) return;
        await using Stream stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > 64 * 1024)
        {
            Status?.Invoke("That file is too large to be a public key.");
            return;
        }
        using var reader = new StreamReader(stream);
        _publicKey.Text = (await reader.ReadToEndAsync()).Trim();
        _provide.IsChecked = true;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", path);
            else Process.Start("xdg-open", path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // No file manager available; the path is shown on the page anyway.
        }
    }

    public void FocusInput() =>
        Dispatcher.UIThread.Post(() => { if (_provide.IsChecked == true) _publicKey.Focus(); }, DispatcherPriority.Background);
}
