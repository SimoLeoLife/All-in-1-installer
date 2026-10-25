using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Settings: the default server and SSH key for every wizard, and importing your own SSH keys.</summary>
public sealed class SettingsView
{
    private sealed record KeyItem(string? Path)
    {
        public override string ToString() => Path is null ? "(no default key)" :
            $"{System.IO.Path.GetFileName(Path)}   (keys/{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))})";
    }

    private readonly TextBox _server = new() { MinWidth = 380, PlaceholderText = "192.168.1.10 or hotel.example.com (add :2222 for another SSH port)" };
    private readonly ComboBox _defaultKey = new() { MinWidth = 380 };
    private readonly ResultBanner _defaultsResult = new();
    private readonly TextBox _importFor = new() { MinWidth = 260, PlaceholderText = "server, e.g. 203.0.113.10" };
    private readonly TextBox _paste = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 130,
        FontFamily = Mono,
        FontSize = 12,
        PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY----- ...   or a public key: ssh-ed25519 AAAA... you@computer"
    };
    private readonly ResultBanner _importResult = new();
    private readonly StackPanel _keyList = new() { Spacing = 6 };
    // One branch list per repository the clone steps use, in the order of the dashboard's component list.
    private readonly ComboBox[] _branches = InstallerWindow.Components
        .Select(_ => new ComboBox { MinWidth = 380, ItemsSource = InstallerSettings.BranchChoices }).ToArray();
    private readonly ResultBanner _branchesResult = new();

    public Control View { get; }

    public SettingsView()
    {
        var page = new StackPanel { Spacing = 18 };
        page.Children.Add(Card(BuildDefaults(), new Thickness(24, 20)));
        page.Children.Add(Card(BuildBranches(), new Thickness(24, 20)));
        page.Children.Add(Card(BuildKeys(), new Thickness(24, 20)));
        page.Children.Add(Card(BuildDownloader(), new Thickness(24, 20)));
        View = page;
    }

    /// <summary>Reloads the settings and the key folder, for when the page is shown again or settings were imported.</summary>
    public void Refresh()
    {
        InstallerSettings settings = InstallerSettings.Load();
        _server.Text = settings.DefaultServer ?? "";
        _downloader.Text = settings.DownloaderPath ?? "";
        ShowDownloaderState();
        var items = new List<KeyItem> { new(null) };
        items.AddRange(ClientConfiguration.AllPrivateKeys().Select(p => new KeyItem(p)));
        _defaultKey.ItemsSource = items;
        _defaultKey.SelectedItem = items.FirstOrDefault(i => i.Path == settings.DefaultKeyPath) ?? items[0];
        if (string.IsNullOrWhiteSpace(_importFor.Text) && settings.DefaultServer is { Length: > 0 } server)
            _importFor.Text = ServerLogin.ParseHost(server).Host;
        for (int i = 0; i < _branches.Length; i++)
            _branches[i].SelectedItem = InstallerSettings.ChoiceFor(settings.BranchFor(InstallerWindow.Components[i].Repository));
        RefreshKeyList();
    }

    private Control BuildDefaults()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Default server", 15, weight: FontWeight.SemiBold));
        panel.Children.Add(Text("Install New Server, AtomCMS and Tools fill in this server. The default key is selected for \"My own SSH key\" " +
                                "and is tried first by AtomCMS and Tools.", 12.5, "Pro.Muted"));
        panel.Children.Add(Label("Server (IP or hostname, with :port for another SSH port)"));
        panel.Children.Add(_server);
        panel.Children.Add(Label("SSH key (from configuration/keys)"));
        panel.Children.Add(_defaultKey);
        var save = new Button { Content = "Save", Padding = new Thickness(22, 9), Classes = { "primary" } };
        var clear = new Button { Content = "Clear", Padding = new Thickness(18, 9), Classes = { "secondary" } };
        save.Click += (_, _) => SaveDefaults();
        clear.Click += (_, _) =>
        {
            InstallerSettings cleared = InstallerSettings.Load();
            cleared.DefaultServer = null;
            cleared.DefaultKey = null;
            cleared.Save();
            Refresh();
            _defaultsResult.Show(ResultKind.Info, "No default server", "The wizards start empty again.");
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(clear);
        panel.Children.Add(buttons);
        panel.Children.Add(_defaultsResult);
        return panel;
    }

    private void SaveDefaults()
    {
        string server = (_server.Text ?? "").Trim();
        if (server.Length > 0)
        {
            (string host, int port) = ServerLogin.ParseHost(server);
            if (host.Length == 0 || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            {
                _defaultsResult.Show(ResultKind.Error, "That is not a valid server", "Use an IP address or hostname, optionally with :port.");
                return;
            }
        }
        string? key = (_defaultKey.SelectedItem as KeyItem)?.Path;
        InstallerSettings settings = InstallerSettings.Load();
        settings.DefaultServer = server.Length > 0 ? server : null;
        settings.DefaultKey = key is null ? null : InstallerSettings.RelativeKey(key);
        settings.Save();
        _defaultsResult.Show(ResultKind.Success, "Saved",
            (server.Length > 0 ? $"The wizards fill in {server}" : "No default server") + (key is null ? "." : $", with the key {Path.GetFileName(key)}."));
    }

    private Control BuildBranches()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Repository branches", 15, weight: FontWeight.SemiBold));
        panel.Children.Add(Text("Which branch Install New Server clones for each repository: main for the stable version, dev for the newest changes. " +
                                "The clone steps start with what is chosen here, and a change made there is saved back to here. " +
                                "An existing clone on another branch is switched over by the step, never silently.", 12.5, "Pro.Muted"));
        for (int i = 0; i < _branches.Length; i++)
        {
            panel.Children.Add(Label($"{InstallerWindow.Components[i].Name}   (github.com/{InstallerWindow.Components[i].Repository})"));
            panel.Children.Add(_branches[i]);
        }
        var save = new Button { Content = "Save", Padding = new Thickness(22, 9), Classes = { "primary" } };
        save.Click += (_, _) => SaveBranches();
        panel.Children.Add(save);
        panel.Children.Add(_branchesResult);
        return panel;
    }

    private void SaveBranches()
    {
        InstallerSettings settings = InstallerSettings.Load();
        for (int i = 0; i < _branches.Length; i++) settings.SetBranch(InstallerWindow.Components[i].Repository, SelectedBranch(i));
        settings.Save();
        _branchesResult.Show(ResultKind.Success, "Saved",
            string.Join(", ", _branches.Select((_, i) => $"{InstallerWindow.Components[i].Name}: {SelectedBranch(i)}")) +
            ". The clone steps of the next installation start with this.");
    }

    private string SelectedBranch(int i) =>
        (_branches[i].SelectedItem as InstallerSettings.BranchChoice ?? InstallerSettings.BranchChoices[0]).Branch;

    private Control BuildKeys()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("SSH keys", 15, weight: FontWeight.SemiBold));
        panel.Children.Add(Text($"Keys in {Path.Combine(ClientConfiguration.Directory, "keys")}: the ones the SSH key step generated, and the ones you import here. " +
                                "Import your own private key to log in with it (\"My own SSH key\"); a public key alone can be installed for root in the SSH key step.",
            12.5, "Pro.Muted"));
        panel.Children.Add(_keyList);

        panel.Children.Add(Text("Import a key", 13.5, weight: FontWeight.SemiBold));
        panel.Children.Add(Label("For server (the folder in configuration/keys)"));
        panel.Children.Add(_importFor);
        var file = new Button { Content = "Import key file ...", Padding = new Thickness(16, 8), Classes = { "secondary" } };
        file.Click += async (_, _) => await ImportFileAsync();
        panel.Children.Add(file);
        panel.Children.Add(Label("Or paste a private key (OpenSSH, PEM, PuTTY .ppk) or a public key"));
        panel.Children.Add(_paste);
        var paste = new Button { Content = "Import pasted key", Padding = new Thickness(16, 8), Classes = { "secondary" } };
        paste.Click += async (_, _) =>
        {
            if (await ImportAsync(_paste.Text ?? "", "pasted key")) _paste.Text = "";
        };
        panel.Children.Add(paste);
        panel.Children.Add(_importResult);
        return panel;
    }

    private void RefreshKeyList()
    {
        _keyList.Children.Clear();
        string? preset = InstallerSettings.Load().DefaultKeyPath;
        string root = Path.Combine(ClientConfiguration.Directory, "keys");
        var files = Directory.Exists(root) ? Directory.GetFiles(root, "*.pub", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList() : [];
        foreach (string pub in files)
        {
            string text;
            try { text = File.ReadAllText(pub); } catch (IOException) { continue; }
            if (!SshKeys.TryParsePublicKey(text, out AuthorizedKey key, out _)) continue;
            string privatePath = pub[..^4];
            bool hasPrivate = File.Exists(privatePath);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 14 };
            row.Children.Add(Text($"keys/{Path.GetFileName(Path.GetDirectoryName(pub))}/{Path.GetFileName(hasPrivate ? privatePath : pub)}", 12.5, weight: FontWeight.SemiBold));
            TextBlock kind = Text(hasPrivate ? (privatePath == preset ? "private key · default" : "private key") : "public key only", 12, hasPrivate ? "Pro.SuccessText" : "Pro.Muted");
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);
            TextBlock detail = Text($"{key.Type}  {key.Fingerprint}", 12, "Pro.Subtle");
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);
            _keyList.Children.Add(row);
        }
        if (_keyList.Children.Count == 0) _keyList.Children.Add(Text("No keys yet.", 12.5, "Pro.Muted"));
    }

    private async Task ImportFileAsync()
    {
        if (TopLevel.GetTopLevel(View) is not { } top) return;
        var options = new FilePickerOpenOptions { Title = "Select an SSH key (private or .pub)", AllowMultiple = false };
        string sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshFolder)) options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(sshFolder);
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0) return;
        await using Stream stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > 64 * 1024)
        {
            _importResult.Show(ResultKind.Error, "That file is too large to be an SSH key", files[0].Name);
            return;
        }
        using var reader = new StreamReader(stream);
        await ImportAsync(await reader.ReadToEndAsync(), files[0].Name);
    }

    // A private key is stored with its public key. A public key on its own is stored as provided_*.pub.
    private async Task<bool> ImportAsync(string text, string source)
    {
        string folder = ServerLogin.ParseHost(_importFor.Text ?? "").Host;
        if (folder.Length == 0)
        {
            _importResult.Show(ResultKind.Error, "For which server?", "Enter the server the key is for: it becomes the folder in configuration/keys.");
            return false;
        }
        if (text.Trim().Length == 0)
        {
            _importResult.Show(ResultKind.Error, "Nothing to import", "Paste a key or choose a key file.");
            return false;
        }

        if (!SshKeys.IsPrivateKey(text))
        {
            if (!SshKeys.TryParsePublicKey(text, out AuthorizedKey publicKey, out string error))
            {
                _importResult.Show(ResultKind.Error, "Not an SSH key", error);
                return false;
            }
            string saved = ClientConfiguration.SavePublicKey(folder, publicKey);
            _importResult.Show(ResultKind.Success, "Public key imported",
                $"{saved}. The SSH key step can install it for root on {folder}. To log in with it, import the private key too.");
            RefreshKeyList();
            return true;
        }

        string passphrase = "";
        AuthorizedKey key;
        while (true)
        {
            try
            {
                key = SshKeys.PublicKeyOf(text, passphrase, $"imported {source}");
                break;
            }
            catch (SshKeys.KeyPasswordException ex)
            {
                if (TopLevel.GetTopLevel(View) is not Window owner) return false;
                string? entered = await PasswordDialog.ShowAsync(owner, "Key password", "This key has a password",
                    passphrase.Length > 0 ? ex.Message + " Try again." : "Enter it once to read the public key. The key is stored as it is: still protected by its password.",
                    confirm: false);
                if (entered is null) return false;
                passphrase = entered;
            }
            catch (FormatException ex)
            {
                _importResult.Show(ResultKind.Error, "Not a usable private key", ex.Message);
                return false;
            }
        }
        string path = ClientConfiguration.SaveImportedKey(folder, text, key);
        _importResult.Show(ResultKind.Success, "Private key imported",
            $"{path} ({key.Type}, {key.Fingerprint}). Choose it as the default key above, or pick it under \"My own SSH key\" when logging in.");
        Refresh();
        return true;
    }

    private readonly TextBox _downloader = new() { PlaceholderText = "e.g. C:\\Tools\\All-in-1\\Habbo Downloader.exe" };
    private readonly ResultBanner _downloaderResult = new();

    private Control BuildDownloader()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("All-in-1 downloader", 15, weight: FontWeight.SemiBold));
        panel.Children.Add(Text("The All-in-1 downloader (github.com/duckietm/all-in-1-converter) saves the Habbo assets in Habbo_Default next to its " +
                                "executable. Addons → Deploy Assets copies them from there to /var/www/gamedata on the server.", 12.5, "Pro.Muted"));
        panel.Children.Add(Label("Executable (Habbo Downloader.exe, or the Linux binary)"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(_downloader);
        var browse = new Button { Content = "Browse ...", Padding = new Thickness(14, 7), Classes = { "secondary" } };
        browse.Click += async (_, _) => await BrowseDownloaderAsync();
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        panel.Children.Add(row);
        var save = new Button { Content = "Save", Padding = new Thickness(22, 9), Classes = { "primary" } };
        save.Click += (_, _) =>
        {
            InstallerSettings settings = InstallerSettings.Load();
            string path = (_downloader.Text ?? "").Trim().Trim('"');
            settings.DownloaderPath = path.Length > 0 ? path : null;
            settings.Save();
            ShowDownloaderState(saved: true);
        };
        panel.Children.Add(save);
        panel.Children.Add(_downloaderResult);
        return panel;
    }

    private void ShowDownloaderState(bool saved = false)
    {
        string path = (_downloader.Text ?? "").Trim().Trim('"');
        if (path.Length == 0)
        {
            if (saved) _downloaderResult.Show(ResultKind.Info, "No downloader set", "Deploy Assets needs it.");
            else _downloaderResult.Hide();
            return;
        }
        if (AssetSync.DownloaderFolder(path) is { } folder)
        {
            int furniture = Directory.Exists(Path.Combine(folder, "Habbo_Default", "hof_furni", "hab"))
                ? Directory.EnumerateFiles(Path.Combine(folder, "Habbo_Default", "hof_furni", "hab"), "*.hab").Count() : 0;
            _downloaderResult.Show(ResultKind.Success, saved ? "Saved" : "All-in-1 downloader found",
                $"{Path.Combine(folder, AssetSync.DefaultFolder)}: {furniture} furniture bundles (.hab) so far.");
        }
        else
            _downloaderResult.Show(ResultKind.Warning, saved ? "Saved, but Habbo_Default is not there" : "Habbo_Default is not next to this file",
                "Choose the downloader's executable; its folder needs Habbo_Default (the downloader makes it on its first run).");
    }

    private async Task BrowseDownloaderAsync()
    {
        if (TopLevel.GetTopLevel(View) is not { } top) return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Select the All-in-1 downloader (Habbo Downloader)", AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        _downloader.Text = path;
        ShowDownloaderState();
    }

    private static TextBlock Label(string text) => Text(text, 12, "Pro.Muted", FontWeight.SemiBold);
}
