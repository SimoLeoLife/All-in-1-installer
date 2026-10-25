using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AllInOneInstaller.Remote;
using AllInOneInstaller.Update;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Main window, with the same sidebar/header/footer shell as the All-in-1 Converter.</summary>
public sealed class InstallerWindow : Window
{
    /// <summary>What a full server installation puts on the server.</summary>
    public static readonly (string Name, string Description, string Repository, string Icon, string Color)[] Components =
    [
        ("Polaris Emulator", "Java 25 game server with a MariaDB database", "duckietm/Polaris-Emulator", Icons.Server, "#4F5FE6"),
        ("Octane", "React browser client (navigator, catalog, chat ...)", "duckietm/Octane", Icons.Cube, "#8B5CF6"),
        ("Octane Renderer", "Draws rooms, avatars and furniture for Octane", "duckietm/Octane-Renderer", Icons.Star, "#0E9F9E")
    ];

    private readonly ContentControl _pageHost = new();
    private readonly ScrollViewer _pageScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _content = new() { Spacing = 18, Margin = new Thickness(32, 0, 32, 18) };
    private readonly TextBlock _title = Text("", 26, weight: FontWeight.SemiBold);
    private readonly TextBlock _subtitle = Text("", 13.5, "Pro.Muted");
    private readonly Border _statusPill = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(11, 5), VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _statusDot = Dot();
    private readonly TextBlock _statusPillText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Text("", 12, "Pro.Muted");
    private readonly TextBlock _connectionFooter = Text("", 12, "Pro.Muted");
    private readonly List<Button> _navigationButtons = [];
    private Button? _dashboardNavigation;
    private Button? _installNavigation;
    private Button? _atomNavigation;
    private Button? _deployNavigation;
    private Button? _explorerNavigation;
    private Button? _sqlNavigation;
    private Button? _toolsNavigation;
    private Button? _settingsNavigation;
    private Button? _aboutNavigation;
    private readonly SettingsView _settings = new();
    private readonly ServerWizard _wizard;
    private readonly ServerWizard _atomWizard;
    private readonly ServerWizard _deployWizard;
    private readonly ServerWizard _explorerWizard;
    private readonly AssetExplorerView _explorer;
    // The explorer is shown instead of its login wizard while that is connected.
    private bool _explorerShown;
    private readonly ServerWizard _sqlWizard;
    private readonly SqlEditorView _sqlEditor;
    // Like the explorer: the editor replaces its login wizard while that is connected.
    private bool _sqlShown;
    private readonly ServerWizard _toolsWizard;
    private readonly ToolsView _tools;
    private ServerWizard[] Wizards => [_wizard, _atomWizard, _deployWizard, _explorerWizard, _sqlWizard, _toolsWizard];
    private bool AnyBusy => Wizards.Any(w => w.IsBusy);
    // The wizard on screen; the header and footer show its connection.
    private ServerWizard Current => _page switch { Page.AtomCms => _atomWizard, Page.DeployAssets => _deployWizard, Page.AssetExplorer => _explorerWizard, Page.SqlEditor => _sqlWizard, Page.Tools or Page.ServerStatus or Page.EmulatorMonitor or Page.Terminal => _toolsWizard, _ => _wizard };

    public InstallerWindow()
    {
        ProfessionalTheme.Install(Application.Current!);
        Title = "All-in-1 Installer";
        Width = 1280;
        Height = 820;
        MinWidth = 1000;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Default;
        this.Bind(BackgroundProperty, "Pro.Surface");

        _wizard = new ServerWizard();
        var atomStep = new AtomCmsStep();
        _atomWizard = new ServerWizard([atomStep],
            (_, _, site, _, steps) => FinishedView.BuildAtomCms(site, atomStep.InstallationKey, atomStep.InstallationCompleted, steps),
            "Web installer", keyLogin: true);
        var syncStep = new AssetSyncStep();
        var textsStep = new UiTextsStep();
        var emulatorAssetsStep = new EmulatorAssetsStep();
        _deployWizard = new ServerWizard([syncStep, textsStep, emulatorAssetsStep],
            (_, _, site, _, steps) => FinishedView.BuildDeploy(site, [syncStep.Summary, textsStep.Summary, emulatorAssetsStep.Summary], steps),
            "Assets on the server", keyLogin: true);
        // Asset Explorer: the wizard only logs in, then the explorer takes the page.
        _explorerWizard = new ServerWizard([], (_, _, _, _, _) => Text("Opening the explorer ...", 13), "Asset Explorer", keyLogin: true, finishTitle: "Explorer");
        _explorer = new AssetExplorerView(() => _explorerWizard.Connection, () => _explorerWizard.DisconnectAndResetAsync());
        _explorerWizard.StateChanged += () => { if (_page == Page.AssetExplorer) ShowAssetExplorer(); };
        _sqlWizard = new ServerWizard([], (_, _, _, _, _) => Text("Opening the SQL editor ...", 13), "SQL Editor", keyLogin: true, finishTitle: "Editor");
        _sqlEditor = new SqlEditorView(() => _sqlWizard.Connection, () => _sqlWizard.DisconnectAndResetAsync());
        _sqlWizard.StateChanged += async () =>
        {
            if (_sqlWizard.Connection is null) await _sqlEditor.CloseSessionAsync();
            if (_page == Page.SqlEditor) ShowSqlEditor();
        };
        // No install steps: the wizard only logs in; the tools above it work while it is connected.
        ToolsView? tools = null;
        _toolsWizard = new ServerWizard([], (_, _, _, _, _) => tools!.Connected, "Use the tools", keyLogin: true, finishTitle: "Connected");
        tools = new ToolsView(() => _toolsWizard.Connection, (busy, status) => _toolsWizard.SetExternalBusy(busy, status));
        _tools = tools;
        _toolsWizard.StateChanged += _tools.Refresh;
        _tools.MonitorRequested += ShowMonitor;
        _tools.TerminalRequested += ShowTerminal;
        _tools.BackRequested += ShowTools;
        foreach (ServerWizard wizard in Wizards)
        {
            wizard.StateChanged += () =>
            {
                RefreshHeader();
                RefreshFooter();
            };
            wizard.Exited += ShowDashboard;
        }
        Content = BuildLayout();
        Closing += (_, e) =>
        {
            if (!AnyBusy) return;
            e.Cancel = true;
            _status.Text = "Wait until the running step has finished before closing.";
        };
        Closed += async (_, _) =>
        {
            await _sqlEditor.CloseSessionAsync();
            foreach (ServerWizard wizard in Wizards) await wizard.DisposeAsync();
        };
        Opened += async (_, _) => await CheckForUpdateAsync();
        ShowDashboard();
    }

    /// <summary>
    /// Asks GitHub at startup whether a newer version of the installer itself was released, and offers to
    /// replace this program with it. Only the published single-file program updates itself; a repository
    /// without releases, or no internet, means nothing is shown.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        AppUpdate.CleanUp();
        if (await AppUpdate.CheckAsync() is not { } update) return;
        _status.Text = $"Version {update.Version} of the installer is available.";
        if (!await UpdateDialog.ShowAsync(this, update)) return;
        AppUpdate.Restart();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private Control BuildLayout()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("236,*") };
        root.Children.Add(BuildSidebar());

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetColumn(main, 1);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(32, 26, 32, 20) };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(_title);
        heading.Children.Add(_subtitle);
        header.Children.Add(heading);
        var pill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        pill.Children.Add(_statusDot);
        pill.Children.Add(_statusPillText);
        _statusPill.Child = pill;
        Grid.SetColumn(_statusPill, 1);
        header.Children.Add(_statusPill);
        main.Children.Add(header);

        _pageScroll.Content = _content;
        Grid.SetRow(_pageHost, 1);
        main.Children.Add(_pageHost);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(32, 9) };
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        footer.Children.Add(_status);
        _connectionFooter.TextWrapping = TextWrapping.NoWrap;
        _connectionFooter.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_connectionFooter, 1);
        footer.Children.Add(_connectionFooter);
        var footerBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }
            .Bind(Border.BorderBrushProperty, "Pro.CardBorder")
            .Bind(Border.BackgroundProperty, "Pro.Card");
        Grid.SetRow(footerBar, 2);
        main.Children.Add(footerBar);

        root.Children.Add(main);
        RefreshFooter();
        return root;
    }

    private Control BuildSidebar()
    {
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new Thickness(6, 0, 0, 26) };
        var logoIcon = Icon(Icons.Server, 19);
        logoIcon.Foreground = Brushes.White;
        brand.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#6D7BFF"), 0), new GradientStop(Color.Parse("#4352D4"), 1) }
            },
            Child = logoIcon
        });
        var brandText = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = "All-in-1 Installer", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeight.SemiBold });
        brandText.Children.Add(new TextBlock { Text = "Hotel server setup", Foreground = Solid(SidebarMuted), FontSize = 11.5 });
        brand.Children.Add(brandText);
        sidebar.Children.Add(brand);

        var nav = new StackPanel { Spacing = 3 };
        _dashboardNavigation = NavButton("Dashboard", Icons.Home, ShowDashboard);
        nav.Children.Add(_dashboardNavigation);
        nav.Children.Add(SidebarLabel("SERVER"));
        _installNavigation = NavButton("Install New Server", Icons.ServerPlus, ShowInstallNewServer);
        nav.Children.Add(_installNavigation);
        nav.Children.Add(SidebarLabel("ADDONS"));
        _atomNavigation = NavButton("AtomCMS", Icons.Puzzle, ShowAtomCms);
        nav.Children.Add(_atomNavigation);
        _deployNavigation = NavButton("Deploy Assets", Icons.Download, ShowDeployAssets);
        nav.Children.Add(_deployNavigation);
        _explorerNavigation = NavButton("Asset Explorer", Icons.FolderSync, () => { _explorerShown = false; ShowAssetExplorer(); });
        nav.Children.Add(_explorerNavigation);
        _sqlNavigation = NavButton("SQL Editor", Icons.Database, () => { _sqlShown = false; ShowSqlEditor(); });
        nav.Children.Add(_sqlNavigation);
        nav.Children.Add(SidebarLabel("TOOLS"));
        _toolsNavigation = NavButton("Tools", Icons.Wrench, ShowTools);
        nav.Children.Add(_toolsNavigation);
        nav.Children.Add(SidebarLabel("GENERAL"));
        _settingsNavigation = NavButton("Settings", Icons.Cog, ShowSettings);
        nav.Children.Add(_settingsNavigation);
        _aboutNavigation = NavButton("About", Icons.Info, ShowAbout);
        nav.Children.Add(_aboutNavigation);
        Grid.SetRow(nav, 1);
        sidebar.Children.Add(nav);

        var footer = new StackPanel { Spacing = 6 };
        footer.Children.Add(new Border { Height = 1, Background = Solid(SidebarHover), Margin = new Thickness(4, 0, 4, 6) });
        footer.Children.Add(new TextBlock
        {
            Text = "Targets Ubuntu Server and Debian",
            Foreground = Solid(SidebarMuted),
            FontSize = 11.5,
            Margin = new Thickness(12, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(footer, 2);
        sidebar.Children.Add(footer);

        return new Border { Background = Solid(Sidebar), Padding = new Thickness(14, 22), Child = sidebar };
    }

    private static TextBlock SidebarLabel(string text) => new()
    {
        Text = text,
        Foreground = Solid(SidebarMuted),
        FontSize = 10.5,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 1,
        Margin = new Thickness(12, 18, 0, 6)
    };

    private Button NavButton(string label, string icon, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(Icon(icon, 17));
        content.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Classes = { "nav" }
        };
        button.Click += (_, _) =>
        {
            if (AnyBusy) return;
            action();
        };
        _navigationButtons.Add(button);
        return button;
    }

    private void ActivateNavigation(Button? selected)
    {
        foreach (Button button in _navigationButtons)
        {
            bool isSelected = button == selected;
            button.Classes.Set("selected", isSelected);
            if (button.Content is StackPanel { Children: [PathIcon icon, TextBlock text] })
            {
                if (isSelected) icon.Foreground = Solid(SidebarAccent);
                else icon.ClearValue(ForegroundProperty);
                text.FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }
    }

    private void ShowScrollPage(params Control[] sections)
    {
        _content.Children.Clear();
        foreach (Control section in sections) _content.Children.Add(section);
        _pageHost.Content = _pageScroll;
        _pageScroll.Offset = default;
    }

    private enum Page { Dashboard, Install, AtomCms, DeployAssets, AssetExplorer, SqlEditor, Tools, ServerStatus, EmulatorMonitor, Terminal, Settings, About }
    private Page _page;

    private void RefreshHeader()
    {
        (_title.Text, _subtitle.Text) = _page switch
        {
            Page.Install => ("Install New Server", "Log in to the Ubuntu / Debian server that will run the hotel."),
            Page.AtomCms => ("AtomCMS", "Addon: AtomCMS as the hotel's website, Octane on /client."),
            Page.AssetExplorer => ("Asset Explorer", _explorerWizard.Connection is { } explorerConnection
                ? $"This computer on the left, {explorerConnection.Login.Host}:{GamedataFiles.Root} on the right."
                : "Addon: manage the files in /var/www/gamedata, with this computer next to it."),
            Page.SqlEditor => ("SQL Editor", _sqlWizard.Connection is { } sqlConnection
                ? $"The databases of {sqlConnection.Login.Host}: browse and edit tables, run queries."
                : "Addon: a database client like HeidiSQL, through the SSH login (no open MariaDB port needed)."),
            Page.DeployAssets => ("Deploy Assets", "Addon: copy the hotel's assets, the client's UI texts and the emulator's images to /var/www/gamedata on the server."),
            Page.Tools => ("Tools", "Restart services, back up and import the database, update, and move your settings."),
            Page.ServerStatus => ("Server status", $"Live figures of {_toolsWizard.Connection?.Login.Host ?? "the server"}, every 2 seconds."),
            Page.Terminal => ("SSH terminal", $"A shell on {_toolsWizard.Connection?.Login.Host ?? "the server"}, logged in like Tools ({_toolsWizard.Connection?.Login.User ?? "root"})."),
            Page.EmulatorMonitor => ("Emulator & wired", $"The emulator on {_toolsWizard.Connection?.Login.Host ?? "the server"}: CPU per thread, the wired engine and slow wired rooms."),
            Page.About => ("About", "All-in-1 Installer"),
            Page.Settings => ("Settings", "Default server and SSH key, your own SSH keys, and the All-in-1 downloader."),
            _ => ("Dashboard", "Set up a complete hotel server over SSH.")
        };

        (string text, string back, string fore) = Current.Connection is { } c
            ? ($"Root on {c.Login.Host}", "Pro.SuccessBack", "Pro.SuccessText")
            : Current.IsBusy
                ? ("Connecting", "Pro.InfoBack", "Pro.InfoText")
                : ("Not connected", "Pro.Hover", "Pro.Muted");
        _statusPillText.Text = text;
        _statusPillText.Bind(TextBlock.ForegroundProperty, fore);
        _statusDot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, fore);
        _statusPill.Bind(Border.BackgroundProperty, back);
    }

    private void RefreshFooter()
    {
        ServerConnection? connection = Current.Connection;
        _connectionFooter.Text = connection is null
            ? "No server connected"
            : $"{connection.LoginUser}@{connection.Login.Host}:{connection.Login.Port}  ·  {connection.Os?.PrettyName}";
        _status.Text = Current.StatusMessage;
    }

    private void ShowDashboard()
    {
        _page = Page.Dashboard;
        ActivateNavigation(_dashboardNavigation);
        ShowScrollPage(
            Tile("Install New Server", "Connect to a fresh Ubuntu Server / Debian machine and install the full hotel stack.",
                Icons.ServerPlus, Brand, ShowInstallNewServer),
            BuildComponentsCard(),
            Text("ADDONS", 11, "Pro.Muted", FontWeight.SemiBold),
            Tile("AtomCMS", "After the installation: AtomCMS (github.com/ObjectRetros/atomcms) as the hotel's website, Octane moves to /client.",
                Icons.Puzzle, Color.Parse("#E8590C"), ShowAtomCms),
            Tile("Deploy Assets", "Sync the furniture, clothes, badges and game data of the All-in-1 downloader to /var/www/gamedata (only new and changed files), and the client's UI texts in your language.",
                Icons.Download, Color.Parse("#8B5CF6"), ShowDeployAssets),
            Tile("Asset Explorer", "Two panes like Norton Commander: this computer and /var/www/gamedata on the server. Upload, download, rename, delete; uploads get the right owner and rights.",
                Icons.FolderSync, Color.Parse("#2563EB"), () => { _explorerShown = false; ShowAssetExplorer(); }),
            Tile("SQL Editor", "A database client like HeidiSQL: databases and tables, browse, filter and edit rows, run queries and scripts, export to CSV. MariaDB is reached through the SSH login.",
                Icons.Database, Color.Parse("#0891B2"), () => { _sqlShown = false; ShowSqlEditor(); }),
            Text("TOOLS", 11, "Pro.Muted", FontWeight.SemiBold),
            Tile("Tools", "Restart services, database backup and import, system / emulator / Octane updates, encrypted settings backup.",
                Icons.Wrench, Color.Parse("#0E9F9E"), ShowTools));
        RefreshHeader();
        RefreshFooter();
    }

    private void ShowInstallNewServer()
    {
        _page = Page.Install;
        ActivateNavigation(_installNavigation);
        _wizard.ApplySettings();
        ShowScrollPage(_wizard.View);
        _wizard.FocusCurrentStep();
        RefreshHeader();
        RefreshFooter();
    }

    private void ShowTools()
    {
        _page = Page.Tools;
        ActivateNavigation(_toolsNavigation);
        _toolsWizard.ApplySettings();
        ShowScrollPage(_toolsWizard.View, _tools.Workspace, _tools.Settings);
        _toolsWizard.FocusCurrentStep();
        RefreshHeader();
        RefreshFooter();
    }

    // Only the dashboard; Back (or Tools in the sidebar) returns to the tiles.
    private void ShowMonitor(ToolsView.Monitor monitor)
    {
        _page = monitor == ToolsView.Monitor.Server ? Page.ServerStatus : Page.EmulatorMonitor;
        ActivateNavigation(_toolsNavigation);
        ShowScrollPage(_tools.MonitorPage(monitor));
        RefreshHeader();
        RefreshFooter();
    }

    // The terminal fills the page (no scrolling around it); Back or Tools returns to the tiles.
    private void ShowTerminal()
    {
        _page = Page.Terminal;
        ActivateNavigation(_toolsNavigation);
        _content.Children.Clear();
        _pageHost.Content = _tools.TerminalPage;
        RefreshHeader();
        RefreshFooter();
    }

    // The login wizard until it is connected, then the explorer filling the page (no scrolling around it).
    private void ShowAssetExplorer()
    {
        bool connected = _explorerWizard.Connection is { IsConnected: true } && !_explorerWizard.IsBusy;
        bool switching = _page != Page.AssetExplorer || connected != _explorerShown;
        _page = Page.AssetExplorer;
        ActivateNavigation(_explorerNavigation);
        if (switching)
        {
            _explorerShown = connected;
            if (connected)
            {
                _content.Children.Clear();
                _pageHost.Content = _explorer;
            }
            else
            {
                _explorerWizard.ApplySettings();
                ShowScrollPage(_explorerWizard.View);
                _explorerWizard.FocusCurrentStep();
            }
        }
        RefreshHeader();
        RefreshFooter();
    }

    // The login wizard until it is connected, then the editor filling the page (no scrolling around it).
    private void ShowSqlEditor()
    {
        bool connected = _sqlWizard.Connection is { IsConnected: true } && !_sqlWizard.IsBusy;
        bool switching = _page != Page.SqlEditor || connected != _sqlShown;
        _page = Page.SqlEditor;
        ActivateNavigation(_sqlNavigation);
        if (switching)
        {
            _sqlShown = connected;
            if (connected)
            {
                _content.Children.Clear();
                _pageHost.Content = _sqlEditor;
            }
            else
            {
                _sqlWizard.ApplySettings();
                ShowScrollPage(_sqlWizard.View);
                _sqlWizard.FocusCurrentStep();
            }
        }
        RefreshHeader();
        RefreshFooter();
    }

    private void ShowDeployAssets()
    {
        _page = Page.DeployAssets;
        ActivateNavigation(_deployNavigation);
        _deployWizard.ApplySettings();
        ShowScrollPage(_deployWizard.View);
        _deployWizard.FocusCurrentStep();
        RefreshHeader();
        RefreshFooter();
    }

    private void ShowAtomCms()
    {
        _page = Page.AtomCms;
        ActivateNavigation(_atomNavigation);
        _atomWizard.ApplySettings();
        ShowScrollPage(_atomWizard.View);
        _atomWizard.FocusCurrentStep();
        RefreshHeader();
        RefreshFooter();
    }

    private void ShowSettings()
    {
        _page = Page.Settings;
        ActivateNavigation(_settingsNavigation);
        _settings.Refresh();
        ShowScrollPage(_settings.View);
        RefreshHeader();
    }

    private void ShowAbout()
    {
        _page = Page.About;
        ActivateNavigation(_aboutNavigation);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text("All-in-1 Installer", 16, weight: FontWeight.SemiBold));
        panel.Children.Add(Text("Installs a full hotel server (Polaris Emulator, Octane and Octane Renderer) on an Ubuntu Server or Debian machine over SSH.", 13, "Pro.Muted"));
        panel.Children.Add(Text("Look and feel based on the All-in-1 Converter by DuckieTM.", 12.5, "Pro.Muted"));
        ShowScrollPage(Card(panel));
        RefreshHeader();
    }

    private Control Tile(string title, string description, string iconPath, Color color, Action open)
    {
        var icon = Icon(iconPath, 26);
        icon.Foreground = Solid(color);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 18 };
        grid.Children.Add(new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = Solid(Color.FromArgb(0x26, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, 17, weight: FontWeight.SemiBold));
        text.Children.Add(Text(description, 12.5, "Pro.Muted"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var chevron = Icon(Icons.ChevronRight, 22).Bind(PathIcon.ForegroundProperty, "Pro.Subtle");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var tile = new Button
        {
            Content = grid,
            Padding = new Thickness(22, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Classes = { "tile" }
        };
        tile.Click += (_, _) =>
        {
            if (AnyBusy) return;
            open();
        };
        return tile;
    }

    private static Control BuildComponentsCard()
    {
        var list = new StackPanel { Spacing = 2 };
        var heading = new StackPanel { Spacing = 2, Margin = new Thickness(12, 8, 12, 10) };
        heading.Children.Add(Text("Full server installation", 15, weight: FontWeight.SemiBold));
        heading.Children.Add(Text("Components the installer sets up", 12, "Pro.Muted"));
        list.Children.Add(heading);
        foreach (var component in Components)
        {
            Color color = Color.Parse(component.Color);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, Margin = new Thickness(12, 8) };
            var icon = Icon(component.Icon, 17);
            icon.Foreground = Solid(color);
            row.Children.Add(new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = Solid(Color.FromArgb(0x26, color.R, color.G, color.B)),
                Child = icon
            });
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Text(component.Name, 13.5, weight: FontWeight.SemiBold));
            text.Children.Add(Text($"{component.Description}  ·  github.com/{component.Repository}", 12, "Pro.Muted"));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chip = Chip("INCLUDED", "Pro.SuccessBack", "Pro.SuccessText");
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);
            list.Children.Add(row);
        }
        return Card(list, new Thickness(12));
    }
}
