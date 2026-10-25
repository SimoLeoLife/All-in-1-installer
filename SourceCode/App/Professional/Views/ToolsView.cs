using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// The Tools page. <see cref="Workspace"/> is always shown but only works once the key-login wizard above it is
/// connected. <see cref="Settings"/> works without a server.
/// </summary>
public sealed class ToolsView
{
    private readonly Func<ServerConnection?> _connection;
    private readonly Action<bool, string> _busy;
    private readonly List<Button> _toolButtons = [];
    private readonly ContentControl _runHost = new();
    private readonly ResultBanner _settingsResult = new();
    private readonly List<Button> _settingsButtons = [];
    private RunPanel? _run;
    private string _runTitle = "";
    private bool _running;

    private readonly TextBlock _hint = Text("Log in to a server above to use these tools.", 12.5, "Pro.WarningText");
    private readonly TextBlock _connectedText = Text("", 13);

    public Control Workspace { get; }
    /// <summary>The login wizard's last page.</summary>
    public Control Connected { get; }
    public Control Settings { get; }

    /// <param name="busy">Marks the page busy with a status line, which hides the buttons and blocks navigation.</param>
    public ToolsView(Func<ServerConnection?> connection, Action<bool, string> busy)
    {
        _connection = connection;
        _busy = busy;
        Workspace = BuildWorkspace();
        Settings = BuildSettings();
        var connected = new StackPanel { Spacing = 6 };
        connected.Children.Add(Text("Connected", 15, weight: FontWeight.SemiBold));
        connected.Children.Add(_connectedText);
        Connected = connected;
        Refresh();
    }

    private Control BuildWorkspace()
    {
        var page = new StackPanel { Spacing = 16 };
        page.Children.Add(Text("Server tools", 15, weight: FontWeight.SemiBold));
        page.Children.Add(_hint);
        page.Children.Add(Section("Status",
            Tool("Server status", "Live CPU, memory, network, disks, users, versions", Icons.Info, ShowStatusAsync),
            Tool("Emulator & wired", "CPU per wired thread and thread group, rooms with slow wired", Icons.Cog, ShowEmulatorMonitorAsync),
            Tool("SSH terminal", "A shell (command line) on the server, in this window", Icons.Console, ShowTerminalAsync)));
        page.Children.Add(Section("Services",
            Tool("Restart emulator", "Disconnects everyone in the hotel", Icons.History, RestartEmulatorAsync),
            Tool("Restart MariaDB", "The emulator reconnects by itself", Icons.Database, RestartMariaDbAsync),
            Tool("Restart NGINX", "And PHP-FPM (AtomCMS)", Icons.Globe, RestartNginxAsync),
            Tool("Restart Traefik", "The Docker container on 80/443", Icons.Swap, RestartTraefikAsync),
            Tool("Restart all", "MariaDB, emulator, NGINX, Traefik", Icons.Play, RestartAllAsync)));
        page.Children.Add(Section("Firewall",
            Tool("Enable firewall", "ufw on: SSH, 80 and 443 only (the Harden step's rules)", Icons.Shield, EnableFirewallAsync),
            Tool("Disable firewall", "ufw off: every port is reachable again", Icons.Alert, DisableFirewallAsync)));
        page.Children.Add(Section("Database",
            Tool("Create full database backup", "Dump on the server, download to this computer", Icons.Download, BackupDatabaseAsync),
            Tool("Import full database", "Replace the hotel database with a backup", Icons.Folder, ImportDatabaseAsync),
            Tool("Recover SQL password", "New MariaDB root password, saved in configuration", Icons.Key, RecoverSqlPasswordAsync)));
        page.Children.Add(Section("Updates",
            Tool("Update system", "apt update, upgrade and dist-upgrade", Icons.Cog, UpdateSystemAsync),
            Tool("Update emulator", "git pull, build.sh, start the new version", Icons.Server, UpdateEmulatorAsync),
            Tool("Update Octane", "Renderer and Octane: git pull, fresh yarn install, build", Icons.Cube, UpdateOctaneAsync)));
        page.Children.Add(_runHost);
        return Card(page, new Thickness(24, 20));
    }

    /// <summary>Enables the server tools while the wizard is connected; call when its state changes.</summary>
    public void Refresh()
    {
        ServerConnection? connection = _connection();
        _hint.IsVisible = connection is null;
        foreach (Button button in _toolButtons)
        {
            button.IsEnabled = !_running && connection is not null;
            button.Opacity = connection is null ? 0.45 : 1;
        }
        foreach (Button button in _settingsButtons) button.IsEnabled = !_running;
        if (connection is not null)
            _connectedText.Text = $"Root on {connection.Login.Host}:{connection.Login.Port} ({connection.Os?.PrettyName}). Pick a tool below.";
    }

    private Control BuildSettings()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Settings backup", 15, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(
            $"All settings of this installer in one file, encrypted with a password: the SSH keys (also the .ppk files) and the MariaDB passwords of every server, from {ClientConfiguration.Directory}. " +
            "Import it on another computer to manage the same servers from there. No server connection needed.", 12.5, "Pro.Muted"));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SettingsButton("Export encrypted settings", Icons.Shield, ExportSettingsAsync));
        buttons.Children.Add(SettingsButton("Import encrypted settings", Icons.Key, ImportSettingsAsync));
        panel.Children.Add(buttons);
        panel.Children.Add(_settingsResult);
        return Card(panel, new Thickness(24, 20));
    }

    private static Control Section(string heading, params Control[] tools)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text(heading.ToUpperInvariant(), 11, "Pro.Muted", FontWeight.SemiBold));
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Control tool in tools) wrap.Children.Add(tool);
        panel.Children.Add(wrap);
        return panel;
    }

    private Button Tool(string title, string description, string icon, Func<Window, ServerConnection, Task> action)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var glyph = Icon(icon, 18);
        glyph.Foreground = Solid(Brand);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(glyph);
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(Text(title, 13, weight: FontWeight.SemiBold));
        text.Children.Add(Text(description, 11.5, "Pro.Subtle"));
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var button = new Button
        {
            Content = content,
            Width = 268,
            Height = 66,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(10),
            Classes = { "secondary" }
        };
        button.Click += async (_, _) => await RunToolAsync(title, action);
        _toolButtons.Add(button);
        return button;
    }

    private Button SettingsButton(string title, string icon, Func<Window, Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var glyph = Icon(icon, 16);
        glyph.Foreground = Solid(Brand);
        content.Children.Add(glyph);
        content.Children.Add(Text(title, 13, weight: FontWeight.SemiBold));
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(16, 9), Classes = { "secondary" } };
        button.Click += async (_, _) =>
        {
            if (_running || TopLevel.GetTopLevel(button) is not Window owner) return;
            SetRunning(true, $"{title} ...");
            try
            {
                await action(owner);
            }
            catch (Exception ex)
            {
                _settingsResult.Show(ResultKind.Error, $"{title} failed", ex.Message);
            }
            finally
            {
                SetRunning(false, "Ready.");
            }
        };
        _settingsButtons.Add(button);
        return button;
    }

    private async Task RunToolAsync(string title, Func<Window, ServerConnection, Task> action)
    {
        if (_running || _connection() is not { } connection || TopLevel.GetTopLevel(Workspace) is not Window owner) return;
        _runTitle = title;
        _run = null;
        SetRunning(true, $"{title} ...");
        try
        {
            await action(owner, connection);
        }
        catch (Exception ex)
        {
            string message = ex is ServerConnectionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (_run is { } run)
            {
                await FlushAsync();
                run.Log("! " + message);
                run.Fail();
                run.Result.Show(ResultKind.Error, $"{title} failed", message);
            }
            else
            {
                Show(title, ResultKind.Error, $"{title} failed", message);
            }
        }
        finally
        {
            if (_run is not null) _run.Busy = false;
            SetRunning(false, _run is null ? "Ready." : $"{title}: finished.");
        }
    }

    private void SetRunning(bool running, string status)
    {
        _running = running;
        _busy(running, status);
        Refresh();
    }

    private RunPanel StartRun(params string[] checks)
    {
        var run = new RunPanel(300, checks);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(_runTitle, 16, weight: FontWeight.SemiBold));
        panel.Children.Add(run);
        _runHost.Content = panel;
        run.Start();
        _run = run;
        ScrollToResult();
        return run;
    }

    // For a tool that was cancelled or refused before anything ran.
    private void Show(string title, ResultKind kind, string heading, string message)
    {
        var banner = new ResultBanner();
        banner.Show(kind, heading, message);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(title, 16, weight: FontWeight.SemiBold));
        panel.Children.Add(banner);
        _runHost.Content = panel;
    }

    private static Task FlushAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private async Task FinishAsync(RunPanel run, ResultKind kind, string heading, string message)
    {
        await FlushAsync();
        run.Result.Show(kind, heading, message);
    }

    private static Task<bool> ConfirmAsync(Window owner, string heading, string message, string go) =>
        ChoiceDialog.ShowAsync(owner, "Are you sure?", heading, message, false,
            new ChoiceDialog.Option<bool>(go, "Go ahead.", true, Primary: true),
            new ChoiceDialog.Option<bool>("Cancel", "Change nothing.", false));

    private static string Size(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";

    // Logs a transfer at every 10 %.
    private static Action<ulong> Progress(RunPanel run, long total)
    {
        int reported = 0;
        return done =>
        {
            int percent = total > 0 ? (int)((long)done * 100 / total) : 0;
            if (percent < reported + 10) return;
            reported = percent / 10 * 10;
            run.Log($"  {reported} %  ({Size((long)done)} of {Size(total)})");
        };
    }

    public enum Monitor { Server, Emulator }

    // Built once; a dashboard samples while it is on screen and stops when its page is left.
    private readonly Dictionary<Monitor, Control> _monitorPages = [];

    /// <summary>A dashboard was clicked: the window shows <see cref="MonitorPage"/> instead of the tiles.</summary>
    public event Action<Monitor>? MonitorRequested;
    /// <summary>The Back button of a dashboard page.</summary>
    public event Action? BackRequested;

    public Control MonitorPage(Monitor monitor)
    {
        if (_monitorPages.TryGetValue(monitor, out Control? page)) return page;
        var back = new Button { Content = "Back to the tools", Padding = new Thickness(18, 9), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        back.Click += (_, _) => BackRequested?.Invoke();
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(monitor == Monitor.Server ? new ServerStatusView(_connection) : new EmulatorMonitorView(_connection));
        panel.Children.Add(back);
        return _monitorPages[monitor] = Card(panel, new Thickness(24, 20));
    }

    private TerminalView? _terminalPage;

    /// <summary>The SSH terminal tile was clicked: the window shows <see cref="TerminalPage"/>.</summary>
    public event Action? TerminalRequested;

    /// <summary>Built once, so the shell keeps running while other pages are shown.</summary>
    public TerminalView TerminalPage
    {
        get
        {
            if (_terminalPage is null)
            {
                _terminalPage = new TerminalView(_connection);
                _terminalPage.BackRequested += () => BackRequested?.Invoke();
            }
            return _terminalPage;
        }
    }

    private Task ShowTerminalAsync(Window owner, ServerConnection connection)
    {
        _run = null;
        TerminalRequested?.Invoke();
        return Task.CompletedTask;
    }

    private Task ShowStatusAsync(Window owner, ServerConnection connection) => ShowMonitor(Monitor.Server);
    private Task ShowEmulatorMonitorAsync(Window owner, ServerConnection connection) => ShowMonitor(Monitor.Emulator);

    private Task ShowMonitor(Monitor monitor)
    {
        _run = null;
        MonitorRequested?.Invoke(monitor);
        return Task.CompletedTask;
    }

    // The result area is under all the buttons: scroll it into view once it has been laid out.
    private void ScrollToResult() =>
        Dispatcher.UIThread.Post(() => (_runHost.Content as Control)?.BringIntoView(), DispatcherPriority.Background);

    private async Task RestartEmulatorAsync(Window owner, ServerConnection connection)
    {
        if (!await ConfirmAsync(owner, "Restart the emulator?", "Everyone in the hotel is disconnected until it has loaded again (about a minute).", "Restart the emulator")) return;
        RunPanel run = StartRun("Restart the emulator");
        await Task.Run(() => ServerTools.RestartEmulatorAsync(connection, run.Log));
        run.Done("loaded");
        await FinishAsync(run, ResultKind.Success, "The emulator is running again", "It has loaded; players can log in again.");
    }

    private async Task RestartMariaDbAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("Restart MariaDB");
        await Task.Run(() => ServerTools.RestartMariaDbAsync(connection, run.Log));
        run.Done("running");
        await FinishAsync(run, ResultKind.Success, "MariaDB is running again",
            "The emulator and AtomCMS reconnect by themselves. If the hotel acts up, restart the emulator too.");
    }

    private async Task RestartNginxAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("Restart NGINX and PHP-FPM");
        await Task.Run(() => ServerTools.RestartNginxAsync(connection, run.Log));
        run.Done("running");
        await FinishAsync(run, ResultKind.Success, "NGINX is running again", $"The site answers on 127.0.0.1:{HotelSite.Port}.");
    }

    private async Task RestartTraefikAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("Restart Traefik");
        await Task.Run(() => ServerTools.RestartTraefikAsync(connection, run.Log));
        run.Done("running");
        await FinishAsync(run, ResultKind.Success, "Traefik is running again", "It answers on ports 80 and 443 again.");
    }

    private async Task EnableFirewallAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("Enable the firewall");
        bool fresh = await Task.Run(() => HardenInstaller.EnableFirewallAsync(connection, run.Log));
        run.Done("active");
        await FinishAsync(run, ResultKind.Success, "The firewall is on",
            (fresh ? "The Harden step's rules are set up: " : "The Harden step's rules are active again: ") +
            $"SSH (port {connection.Login.Port}, rate limited), HTTP and HTTPS (only from Cloudflare when the site uses it) are open; MariaDB and the emulator's ports are closed (HeidiSQL works through an SSH tunnel).");
    }

    private async Task DisableFirewallAsync(Window owner, ServerConnection connection)
    {
        if (!await ConfirmAsync(owner, "Switch the firewall off?",
                "Every port on the server can be reached from the internet again, also the emulator's own ports (3000, 2096) and anything else that listens. " +
                "Use it to test a connection problem, and switch it on again afterwards. The rules are kept.", "Disable firewall")) return;
        RunPanel run = StartRun("Disable the firewall");
        await Task.Run(() => HardenInstaller.DisableFirewallAsync(connection, run.Log));
        run.Done("inactive");
        await FinishAsync(run, ResultKind.Warning, "The firewall is off",
            "All ports are open. Click Enable firewall to switch the same rules on again.");
    }

    private async Task RestartAllAsync(Window owner, ServerConnection connection)
    {
        if (!await ConfirmAsync(owner, "Restart everything?", "MariaDB, the emulator, NGINX (with PHP-FPM) and Traefik restart one after the other. Everyone in the hotel is disconnected.", "Restart all")) return;
        RunPanel run = StartRun("MariaDB", "Emulator", "NGINX and PHP-FPM", "Traefik");
        // MariaDB first, so the emulator starts with fresh connections.
        await Task.Run(() => ServerTools.RestartMariaDbAsync(connection, run.Log));
        run.Done("running");
        await Task.Run(() => ServerTools.RestartEmulatorAsync(connection, run.Log));
        run.Done("loaded");
        await Task.Run(() => ServerTools.RestartNginxAsync(connection, run.Log));
        run.Done("running");
        await Task.Run(() => ServerTools.RestartTraefikAsync(connection, run.Log));
        run.Done("running");
        await FinishAsync(run, ResultKind.Success, "Everything is running again", "MariaDB, the emulator, NGINX and Traefik were restarted.");
    }

    private async Task BackupDatabaseAsync(Window owner, ServerConnection connection)
    {
        IStorageFile? target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the database backup as",
            SuggestedFileName = $"habbo-{connection.Login.Host}-{DateTime.Now:yyyyMMdd-HHmmss}.sql.gz",
            DefaultExtension = "gz",
            FileTypeChoices = [new FilePickerFileType("Compressed SQL dump") { Patterns = ["*.sql.gz"] }]
        });
        if (target?.TryGetLocalPath() is not { } local) return;

        RunPanel run = StartRun("Dump the database on the server", "Download to this computer");
        DatabaseBackup backup = await Task.Run(() => ServerTools.BackupDatabaseAsync(connection, run.Log));
        run.Done($"{backup.Database}, {Size(backup.Bytes)}");

        run.Log($"> Downloading {backup.RemotePath} to {local}");
        await connection.DownloadAsync(backup.RemotePath, local, Progress(run, backup.Bytes));
        // The dump holds every password hash of the hotel.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(local, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        run.Log($"  {Size(new FileInfo(local).Length)} saved");
        run.Done(Path.GetFileName(local));
        await FinishAsync(run, ResultKind.Success, "The database backup is on this computer",
            $"{local} ({Size(backup.Bytes)}). A copy stays on the server in {ServerTools.BackupDirectory} (the newest {ServerTools.KeptBackups} are kept). " +
            "Restore it with Import full database.");
    }

    private async Task ImportDatabaseAsync(Window owner, ServerConnection connection)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the database backup to import",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQL dumps") { Patterns = ["*.sql.gz", "*.sql", "*.gz"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } local) return;
        long bytes = new FileInfo(local).Length;
        if (!await ConfirmAsync(owner, "Replace the hotel database?",
                $"The hotel database on {connection.Login.Host} is deleted and replaced with {Path.GetFileName(local)} ({Size(bytes)}). " +
                $"A safety backup of the current database is made first, in {ServerTools.BackupDirectory}. The emulator is stopped during the import.",
                "Replace the database")) return;

        RunPanel run = StartRun("Upload to the server", "Safety backup, import, start the emulator");
        string remote = $"{ServerTools.BackupDirectory}/import-{DateTime.Now:yyyyMMdd-HHmmss}{(local.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? ".sql.gz" : ".sql")}";
        await connection.RunAsRootAsync($"install -d -m 700 {ServerTools.BackupDirectory}");
        run.Log($"> Uploading {Path.GetFileName(local)} ({Size(bytes)}) to {remote}");
        await connection.UploadAsync(local, remote, Progress(run, bytes));
        run.Done(Size(bytes));

        await Task.Run(() => ServerTools.ImportDatabaseAsync(connection, remote, run.Log));
        run.Done("imported");
        await FinishAsync(run, ResultKind.Success, "The database is imported",
            $"{Path.GetFileName(local)} is now the hotel database; the emulator has started with it. The database as it was is in {ServerTools.BackupDirectory}/*-before-import-*.sql.gz.");
    }

    private async Task RecoverSqlPasswordAsync(Window owner, ServerConnection connection)
    {
        string host = connection.Login.Host;
        string path = ClientConfiguration.DatabaseCredentialsPath(host);
        DatabaseCredentials? saved = ClientConfiguration.LoadDatabaseCredentials(host);
        if (!await ConfirmAsync(owner, "Reset the MariaDB root password?",
                $"root on {host} gets a new random password, saved in {path}" + (saved is null ? "." : " (the old file is kept next to it as .bak-<time>).") +
                " The emulator and AtomCMS log in with their own accounts and keep working. HeidiSQL and other tools need the new password. " +
                "When root cannot log in through the socket any more, MariaDB restarts for a moment without privilege checks to change it.",
                "Reset the password")) return;

        RunPanel run = StartRun("Reset the root password on the server", "Test the new password", "Save it in the configuration folder");
        DatabaseCredentials credentials;
        if (saved is not null)
        {
            credentials = saved with { RootPassword = DatabaseCredentials.NewPassword() };
        }
        else
        {
            // No file yet: take the other accounts from the server so the new file is complete.
            run.Log("> No saved MariaDB file for this server: reading the CMS and emulator accounts from the server");
            var accounts = await Task.Run(() => MariaDbInstaller.ReadAccountsAsync(connection));
            DatabaseCredentials generated = DatabaseCredentials.Generate(host);
            credentials = generated with
            {
                Database = accounts.Database ?? generated.Database,
                CmsUser = accounts.CmsUser ?? generated.CmsUser,
                CmsPassword = accounts.CmsPassword ?? generated.CmsPassword,
                EmulatorUser = accounts.EmulatorUser ?? generated.EmulatorUser,
                EmulatorPassword = accounts.EmulatorPassword ?? ""
            };
            run.Log($"  CMS account: {(accounts.CmsPassword is null ? "not found, a new password is saved (the MariaDB step sets it)" : $"{credentials.CmsUser} from AtomCMS' .env")}");
            run.Log($"  Emulator account: {(accounts.EmulatorPassword is null ? "not found" : $"{credentials.EmulatorUser} from config.ini")}");
        }

        await Task.Run(() => MariaDbInstaller.ResetRootPasswordAsync(connection, credentials, run.Log));
        run.Done("changed");
        run.Log("> Logging in with the new password");
        // 127.0.0.1 (SSH tunnels) and the socket must work. root@'%' only exists on servers this installer set up.
        string? problem = await Task.Run(() => MariaDbInstaller.TestLoginAsync(connection, credentials.RootUser, credentials.RootPassword, null,
            local: true, run.Log, remote: false));
        if (problem is not null) throw new ServerConnectionException(problem);
        string? remoteProblem = await Task.Run(() => MariaDbInstaller.TestLoginAsync(connection, credentials.RootUser, credentials.RootPassword, null,
            local: false, run.Log));
        if (remoteProblem is not null) run.Log($"  ({remoteProblem}: not needed for SSH tunnels)");
        run.Done("works");

        string file = ClientConfiguration.SaveDatabaseCredentials(credentials);
        run.Log($"  Saved in {file}");
        run.Done(Path.GetFileName(file));
        await FinishAsync(run, ResultKind.Success, "The MariaDB root password is reset",
            $"The new password of {credentials.RootUser} is in {file}. The SQL Editor uses it from now on; " +
            "for HeidiSQL, connect through an SSH tunnel to 127.0.0.1:3306 with that password.");
    }

    private async Task UpdateSystemAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("apt-get update, upgrade and dist-upgrade");
        bool reboot = await Task.Run(() => ServerTools.UpdateSystemAsync(connection, run.Log));
        run.Done(reboot ? "reboot needed" : "up to date");
        if (reboot)
            await FinishAsync(run, ResultKind.Warning, "The system is updated; a reboot is needed",
                "Reboot the server when it suits you (sudo reboot). The emulator, MariaDB, NGINX and Traefik start again by themselves.");
        else
            await FinishAsync(run, ResultKind.Success, "The system is up to date", "All packages are updated (changed configuration files were kept).");
    }

    private async Task UpdateEmulatorAsync(Window owner, ServerConnection connection)
    {
        if (!await ConfirmAsync(owner, "Update the emulator?",
                "git pull in /var/www/emulator. When there is something new, the emulator is built again and restarted with the new version: everyone in the hotel is disconnected for a moment.",
                "Update the emulator")) return;
        RunPanel run = StartRun("git pull", "build.sh", "Start the new version");
        (string? oldCommit, string? oldVersion) = await Task.Run(() => ServerTools.EmulatorVersionAsync(connection));
        string commit = await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, EmulatorInstaller.Repository, run.Log));
        // New images or badge parts in the emulator's Assets/ go to gamedata (missing files only).
        await Task.Run(() => EmulatorAssetsInstaller.InstallAsync(connection, run.Log));
        (string? newCommit, _) = await Task.Run(() => ServerTools.EmulatorVersionAsync(connection));
        run.Done(commit);

        if (newCommit == oldCommit && oldVersion is not null)
        {
            run.Log($"  No new commits: {oldVersion} stays.");
            run.Done("not needed");
            run.Done("not needed");
            await FinishAsync(run, ResultKind.Success, "The emulator is up to date", $"{oldVersion} ({oldCommit}) is the newest version; nothing was rebuilt or restarted.");
            return;
        }

        await Task.Run(() => ServerTools.FixEmulatorOwnerAsync(connection, run.Log));
        string javaHome = await Task.Run(() => ServerTools.JavaHomeAsync(connection));
        string jar = await Task.Run(() => EmulatorInstaller.BuildAsync(connection, javaHome, run.Log));
        (_, string? newVersion) = await Task.Run(() => ServerTools.EmulatorVersionAsync(connection));
        run.Done(newVersion ?? Path.GetFileName(jar));

        // The start script runs the newest target/*-jar-with-dependencies.jar, so a new version needs no changes.
        await Task.Run(() => ServerTools.RestartEmulatorAsync(connection, run.Log));
        run.Done("loaded");
        await FinishAsync(run, ResultKind.Success, "The emulator is updated",
            oldVersion == newVersion ? $"{newVersion} was rebuilt with the new commits ({oldCommit} → {newCommit}) and is running."
                : $"{oldVersion ?? "?"} → {newVersion} ({oldCommit} → {newCommit}) is running.");
    }

    private async Task UpdateOctaneAsync(Window owner, ServerConnection connection)
    {
        RunPanel run = StartRun("git pull Octane-Renderer and Octane", "Octane-Renderer: fresh yarn install", "Octane: fresh yarn install",
            "Configuration for the site", "yarn build", "Serve and test");
        OctaneState state = await Task.Run(() => OctaneInstaller.InspectAsync(connection));
        WebAccessSettings site = state.Site ?? throw new ServerConnectionException("The hotel's address is not known: run the Web access step first.");
        if (!state.OctanePresent || !state.RendererPresent) throw new ServerConnectionException("Octane or Octane-Renderer is not cloned on this server.");

        string renderer = await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, OctaneInstaller.Renderer, run.Log));
        string octane = await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, OctaneInstaller.Octane, run.Log));
        run.Done(octane);

        await Task.Run(() => OctaneInstaller.YarnInstallAsync(connection, OctaneInstaller.Renderer, run.Log, fresh: true));
        run.Done("done");
        await Task.Run(() => OctaneInstaller.YarnInstallAsync(connection, OctaneInstaller.Octane, run.Log, fresh: true));
        run.Done("done");

        // Also puts back the /client/ --base in package.json that AtomCMS needs.
        await Task.Run(() => OctaneInstaller.ConfigureAsync(connection, site, run.Log));
        run.Done(site.ClientUrl);
        await Task.Run(() => OctaneInstaller.BuildAsync(connection, site, run.Log));
        run.Done("dist/");
        await Task.Run(() => OctaneInstaller.PublishAsync(connection, site, run.Log));
        run.Done(site.ClientUrl);
        await FinishAsync(run, ResultKind.Success, $"Octane is updated on {site.ClientUrl}",
            $"Octane-Renderer {renderer}, Octane {octane}. Players get the new client when they reload the page.");
    }

    private async Task ExportSettingsAsync(Window owner)
    {
        IStorageFile? target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the encrypted settings as",
            SuggestedFileName = $"all-in-1-settings-{DateTime.Now:yyyyMMdd-HHmmss}{SettingsBackup.Extension}",
            DefaultExtension = SettingsBackup.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("All-in-1 settings") { Patterns = ["*" + SettingsBackup.Extension] }]
        });
        if (target?.TryGetLocalPath() is not { } file) return;
        string? password = await PasswordDialog.ShowAsync(owner, "Encrypt the settings", "Choose a password",
            "The file holds your SSH keys and database passwords: it can only be opened with this password. Keep it somewhere safe; it cannot be recovered.",
            confirm: true, SettingsBackup.MinimumPasswordLength);
        if (password is null) return;
        int count = await Task.Run(() => SettingsBackup.Export(file, password));
        _settingsResult.Show(ResultKind.Success, "The settings are exported",
            $"{count} files, encrypted, in {file}. Import them on the other computer with Import encrypted settings and the same password.");
    }

    private async Task ImportSettingsAsync(Window owner)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the encrypted settings",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("All-in-1 settings") { Patterns = ["*" + SettingsBackup.Extension] }]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } file) return;
        string? password = await PasswordDialog.ShowAsync(owner, "Decrypt the settings", "Password of the settings file",
            $"The password chosen when {Path.GetFileName(file)} was exported.", confirm: false);
        if (password is null) return;
        if (!await ConfirmAsync(owner, "Replace the settings on this computer?",
                $"The SSH keys and database passwords in {ClientConfiguration.Directory} are replaced with those in the file. The current ones are kept next to it as configuration.bak-<time>.",
                "Import the settings")) return;
        (string? previous, int count) = await Task.Run(() => SettingsBackup.Import(file, password));
        _settingsResult.Show(ResultKind.Success, "The settings are imported",
            $"{count} files in {ClientConfiguration.Directory}." + (previous is null ? "" : $" The previous settings are in {previous}."));
    }
}
