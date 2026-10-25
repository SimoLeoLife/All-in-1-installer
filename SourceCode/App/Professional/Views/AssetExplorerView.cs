using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// Two-pane file manager in the style of Norton Commander. The left pane is this computer and the right pane is the
/// server, locked to /var/www/gamedata. Uploads get owner www-data, folders 2775 and files 664.
/// </summary>
public sealed class AssetExplorerView : Grid
{
    private static readonly string[] ImageExtensions = [".png", ".gif", ".jpg", ".jpeg", ".bmp", ".webp"];

    private readonly Func<ServerConnection?> _connection;
    private readonly Func<Task> _disconnect;
    private readonly Pane _local;
    private readonly Pane _remote;
    private Pane _active;
    private readonly TextBlock _title = Text("", 13, weight: FontWeight.SemiBold);
    private readonly TextBlock _status = Text("", 12.5, "Pro.Muted");
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Width = 120, Height = 90 };
    private readonly TextBlock _previewText = Text("", 12, "Pro.Muted");
    private readonly Button _copy = KeyButton("F5", "Upload →");
    private readonly List<Button> _actions = [];
    private bool _busy;
    private int _previewRequest;
    private ServerConnection? _loadedFor;

    public AssetExplorerView(Func<ServerConnection?> connection, Func<Task> disconnect)
    {
        _connection = connection;
        _disconnect = disconnect;
        RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto");
        RowSpacing = 10;
        Margin = new Thickness(32, 0, 32, 18);

        _local = new Pane(this, local: true);
        _remote = new Pane(this, local: false);
        _active = _local;

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(_title);
        heading.Children.Add(Text($"The server side is locked to {GamedataFiles.Root}: uploads get owner www-data, folders 2775, files 664.", 12, "Pro.Muted"));
        bar.Children.Add(heading);
        var disconnectButton = new Button { Content = "Disconnect", Padding = new Thickness(14, 7), Classes = { "secondary" } };
        disconnectButton.Click += async (_, _) => { if (!_busy) await _disconnect(); };
        Grid.SetColumn(disconnectButton, 1);
        bar.Children.Add(disconnectButton);
        Children.Add(bar);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        panes.Children.Add(_local);
        Grid.SetColumn(_remote, 1);
        panes.Children.Add(_remote);
        Grid.SetRow(panes, 1);
        Children.Add(panes);

        var info = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        var previewFrame = new Border { Width = 132, Height = 100, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = _preview, BorderThickness = new Thickness(1) }
            .Bind(Border.BackgroundProperty, "Pro.Surface").Bind(Border.BorderBrushProperty, "Pro.CardBorder");
        info.Children.Add(previewFrame);
        var lines = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _previewText.TextWrapping = TextWrapping.Wrap;
        _status.TextWrapping = TextWrapping.Wrap;
        lines.Children.Add(_previewText);
        lines.Children.Add(_status);
        lines.Children.Add(_progress);
        Grid.SetColumn(lines, 1);
        info.Children.Add(lines);
        Grid.SetRow(info, 2);
        Children.Add(info);

        var keys = new WrapPanel { Orientation = Orientation.Horizontal };
        Button open = KeyButton("Enter", "Open");
        open.Click += async (_, _) => await _active.OpenSelectedAsync();
        _copy.Click += async (_, _) => await CopyAsync();
        Button rename = KeyButton("F6", "Rename");
        rename.Click += async (_, _) => await RenameAsync();
        Button folder = KeyButton("F7", "New folder");
        folder.Click += async (_, _) => await NewFolderAsync();
        Button delete = KeyButton("F8", "Delete");
        delete.Click += async (_, _) => await DeleteAsync();
        Button refresh = KeyButton("Ctrl+R", "Refresh");
        refresh.Click += async (_, _) => await RefreshAsync();
        foreach (Button button in new[] { open, _copy, rename, folder, delete, refresh })
        {
            keys.Children.Add(button);
            _actions.Add(button);
        }
        keys.Children.Add(Text("   Tab switches panes · Backspace goes up · Ctrl/Shift+click selects more · double-click opens · drag files between the panes, or from Explorer onto the server", 11.5, "Pro.Subtle"));
        Grid.SetRow(keys, 3);
        Children.Add(keys);

        Activate(_local);
        AttachedToVisualTree += async (_, _) => await ConnectedAsync();
    }

    private static Button KeyButton(string key, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Text(key, 11.5, "Pro.AccentText", FontWeight.Bold));
        content.Children.Add(Text(label, 12.5));
        return new Button { Content = content, Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 8, 6), Classes = { "secondary" } };
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async Task ConnectedAsync()
    {
        ServerConnection? connection = _connection();
        if (connection is null) return;
        _title.Text = $"This computer  ⇄  {connection.Login.User}@{connection.Login.Host}:{GamedataFiles.Root}";
        if (_loadedFor == connection) return;
        _loadedFor = connection;
        _remote.Location = "";
        if (_local.Location.Length == 0 && !OperatingSystem.IsWindows())
            _local.Location = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await _local.LoadAsync();
        await _remote.LoadAsync();
        _local.FocusList();
    }

    private void Activate(Pane pane)
    {
        _active = pane;
        _local.SetActive(pane == _local);
        _remote.SetActive(pane == _remote);
        ((StackPanel)_copy.Content!).Children[1] = Text(pane.IsLocal ? "Upload →" : "← Download", 12.5);
    }

    private Pane Other => _active == _local ? _remote : _local;

    private void ShowStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, error ? "Pro.ErrorText" : "Pro.Muted");
    }

    private async Task RunAsync(string what, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        foreach (Button button in _actions) button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ServerConnectionException or IOException or UnauthorizedAccessException or ArgumentException
                                       or Renci.SshNet.Common.SshException or InvalidOperationException)
        {
            ShowStatus($"{what} failed: {ex.Message}", error: true);
        }
        finally
        {
            _busy = false;
            _progress.IsVisible = false;
            foreach (Button button in _actions) button.IsEnabled = true;
            _active.FocusList();
        }
    }

    private async Task RefreshAsync() => await RunAsync("Refreshing", async () =>
    {
        await _local.LoadAsync();
        await _remote.LoadAsync();
        ShowStatus("Refreshed.");
    });

    private async Task CopyAsync()
    {
        List<Item> items = _active.SelectedItems();
        if (items.Count == 0) { ShowStatus("Select files or folders first."); return; }
        if (_active.IsLocal) await UploadAsync(items.Select(i => i.FullPath).ToList(), _remote.Location);
        else
        {
            if (_local.Location.Length == 0) { ShowStatus("Open a folder on this computer (left) to download into.", error: true); return; }
            await DownloadAsync(items.Select(i => i.Name).ToList(), _remote.Location, _local.Location);
        }
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir)
    {
        ServerConnection? connection = _connection();
        if (connection is null || Owner is not { } owner || _busy || localPaths.Count == 0) return;
        var names = localPaths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToList();
        // A drop on a folder row targets a folder other than the one on screen, so list it from the server.
        HashSet<string> existing;
        try
        {
            existing = remoteDir == _remote.Location
                ? [.. _remote.Items.Where(i => !i.IsParent).Select(i => i.Name)]
                : [.. (await Task.Run(() => GamedataFiles.ListAsync(connection, remoteDir))).Select(e => e.Name)];
        }
        catch (ServerConnectionException ex) { ShowStatus("Uploading failed: " + ex.Message, error: true); return; }
        List<string> clashes = names.Where(existing.Contains).ToList();
        bool skip = false;
        if (clashes.Count > 0)
        {
            int? answer = await AskOverwriteAsync(owner, clashes, GamedataFiles.Absolute(remoteDir));
            if (answer is null) return;
            skip = answer == 1;
        }
        await RunAsync("Uploading", async () =>
        {
            long total = localPaths.Sum(LocalSize);
            string target = GamedataFiles.Absolute(remoteDir);
            ShowProgress($"Uploading {localPaths.Count} item(s), {ServerStatusView.Bytes(total)}, to {target} ...", 0, total);
            var started = DateTime.UtcNow;
            await Task.Run(() => GamedataFiles.UploadAsync(connection, localPaths, remoteDir, skip,
                sent => Dispatcher.UIThread.Post(() => ShowProgress(
                    $"Uploading to {target}: {ServerStatusView.Bytes(sent)} of {ServerStatusView.Bytes(total)} · {ServerStatusView.Rate(sent / Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds))}",
                    sent, total))));
            if (_remote.Location == remoteDir) await _remote.LoadAsync(select: names);
            ShowStatus($"Uploaded {localPaths.Count} item(s) ({ServerStatusView.Bytes(total)}) to {target}" +
                       (skip ? ", existing files kept" : "") + ". Rights set: owner www-data, folders 2775, files 664.");
        });
    }

    private async Task DownloadAsync(IReadOnlyList<string> names, string remoteDir, string localDir)
    {
        ServerConnection? connection = _connection();
        if (connection is null || Owner is not { } owner || _busy || names.Count == 0) return;
        List<(string Path, long Size)> files = [];
        await RunAsync("Reading the files", async () =>
            files = await Task.Run(() => GamedataFiles.FilesUnderAsync(connection, remoteDir, names)));
        if (files.Count == 0) { if (!_status.Text!.Contains("failed")) ShowStatus("There are no files to download in the selection."); return; }

        var map = files.Select(f => (Remote: GamedataFiles.Absolute(remoteDir) + "/" + f.Path,
            Local: Path.Combine([localDir, .. f.Path.Split('/')]), f.Size)).ToList();
        List<string> clashes = map.Where(m => File.Exists(m.Local)).Select(m => Path.GetRelativePath(localDir, m.Local)).ToList();
        if (clashes.Count > 0)
        {
            int? answer = await AskOverwriteAsync(owner, clashes, localDir);
            if (answer is null) return;
            if (answer == 1) map = map.Where(m => !File.Exists(m.Local)).ToList();
        }
        await RunAsync("Downloading", async () =>
        {
            long total = map.Sum(m => m.Size);
            var started = DateTime.UtcNow;
            ShowProgress($"Downloading {map.Count} file(s), {ServerStatusView.Bytes(total)}, to {localDir} ...", 0, total);
            await connection.DownloadFilesAsync(map.Select(m => (m.Remote, m.Local)).ToList(),
                done => Dispatcher.UIThread.Post(() => ShowProgress(
                    $"Downloading to {localDir}: {ServerStatusView.Bytes(done)} of {ServerStatusView.Bytes(total)} · {ServerStatusView.Rate(done / Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds))}",
                    done, total)));
            if (_local.Location == localDir) await _local.LoadAsync(select: names);
            ShowStatus($"Downloaded {map.Count} file(s) ({ServerStatusView.Bytes(total)}) to {localDir}.");
        });
    }

    private void ShowProgress(string text, long done, long total)
    {
        _progress.IsVisible = true;
        _progress.Value = total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);
        ShowStatus(text);
    }

    private static long LocalSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Returns 0 to replace, 1 to keep the existing files, or null to cancel.
    private static Task<int?> AskOverwriteAsync(Window owner, List<string> clashes, string where) =>
        ChoiceDialog.ShowAsync<int?>(owner, "Already there", $"{clashes.Count} item(s) already exist in {where}",
            string.Join(", ", clashes.Take(8)) + (clashes.Count > 8 ? $" and {clashes.Count - 8} more" : "") + ".", null,
            new ChoiceDialog.Option<int?>("Replace them", "The copies here replace the files there.", 0, Primary: true),
            new ChoiceDialog.Option<int?>("Keep the existing files", "Only copy what is not there yet.", 1),
            new ChoiceDialog.Option<int?>("Cancel", "Copy nothing.", null));

    private async Task RenameAsync()
    {
        List<Item> items = _active.SelectedItems();
        if (items.Count != 1 || items[0].IsDrive) { ShowStatus("Select one file or folder to rename."); return; }
        if (Owner is not { } owner) return;
        Item item = items[0];
        Pane pane = _active;
        string location = pane.Location;
        string? name = await NameDialog.ShowAsync(owner, "Rename", $"New name for {item.Name}:", item.Name, "Rename", NameProblem);
        if (name is null || name == item.Name) return;
        await RunAsync("Renaming", async () =>
        {
            if (pane.IsLocal)
            {
                string target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
                if (Directory.Exists(item.FullPath)) Directory.Move(item.FullPath, target); else File.Move(item.FullPath, target);
            }
            else if (_connection() is { } connection)
                await Task.Run(() => GamedataFiles.RenameAsync(connection, location, item.Name, name));
            if (pane.Location == location) await pane.LoadAsync(select: [name]);
            ShowStatus($"Renamed {item.Name} to {name}.");
        });
    }

    private async Task NewFolderAsync()
    {
        if (_active.IsLocal && _local.Location.Length == 0) { ShowStatus("Open a drive or folder first."); return; }
        if (Owner is not { } owner) return;
        Pane pane = _active;
        string location = pane.Location;
        string? name = await NameDialog.ShowAsync(owner, "New folder",
            pane.IsLocal ? $"In {location}:" : $"In {GamedataFiles.Absolute(location)} (owner www-data, rights 2775):",
            "", "Create", NameProblem);
        if (name is null) return;
        await RunAsync("Creating the folder", async () =>
        {
            if (pane.IsLocal) Directory.CreateDirectory(Path.Combine(location, name));
            else if (_connection() is { } connection)
                await Task.Run(() => GamedataFiles.CreateFolderAsync(connection, location, name));
            if (pane.Location == location) await pane.LoadAsync(select: [name]);
            ShowStatus($"Created {name}.");
        });
    }

    private string? NameProblem(string name)
    {
        if (GamedataFiles.NameProblem(name) is { } problem) return problem;
        if (_active.IsLocal && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "That name has characters this computer does not allow.";
        if (_active.Items.Any(i => !i.IsParent && string.Equals(i.Name, name, StringComparison.Ordinal))) return $"{name} already exists here.";
        return null;
    }

    private async Task DeleteAsync()
    {
        List<Item> items = _active.SelectedItems().Where(i => !i.IsDrive).ToList();
        if (items.Count == 0) { ShowStatus("Select files or folders to delete."); return; }
        if (Owner is not { } owner) return;
        Pane pane = _active;
        string location = pane.Location;
        string where = pane.IsLocal ? "from this computer" : $"from the server ({GamedataFiles.Absolute(location)})";
        bool go = await ChoiceDialog.ShowAsync(owner, "Delete", $"Delete {items.Count} item(s) {where}?",
            string.Join(", ", items.Take(10).Select(i => i.IsDirectory ? i.Name + "/ (with everything in it)" : i.Name)) +
            (items.Count > 10 ? $" and {items.Count - 10} more" : "") + ". This cannot be undone.", false,
            new ChoiceDialog.Option<bool>("Delete", "Remove them for good.", true, Primary: true),
            new ChoiceDialog.Option<bool>("Cancel", "Keep them.", false));
        if (!go) return;
        await RunAsync("Deleting", async () =>
        {
            if (pane.IsLocal)
                foreach (Item item in items)
                {
                    if (Directory.Exists(item.FullPath)) Directory.Delete(item.FullPath, recursive: true);
                    else File.Delete(item.FullPath);
                }
            else if (_connection() is { } connection)
                await Task.Run(() => GamedataFiles.DeleteAsync(connection, location, items.Select(i => i.Name).ToList()));
            if (pane.Location == location) await pane.LoadAsync();
            ShowStatus($"Deleted {items.Count} item(s).");
        });
    }

    private async Task PreviewAsync(Pane pane)
    {
        int request = ++_previewRequest;
        List<Item> items = pane.SelectedItems();
        Item? item = items.Count == 1 ? items[0] : null;
        if (item is null || item.IsDirectory)
        {
            _preview.Source = null;
            _previewText.Text = items.Count > 1 ? $"{items.Count} selected · {ServerStatusView.Bytes(items.Where(i => !i.IsDirectory).Sum(i => i.Size))} in files" : "";
            return;
        }
        string info = $"{item.Name} · {ServerStatusView.Bytes(item.Size)} · {item.Modified:yyyy-MM-dd HH:mm}";
        _previewText.Text = info;
        _preview.Source = null;
        if (!ImageExtensions.Contains(Path.GetExtension(item.Name).ToLowerInvariant())) return;
        try
        {
            byte[]? bytes = pane.IsLocal
                ? item.Size <= 8 << 20 ? await File.ReadAllBytesAsync(item.FullPath) : null
                : _connection() is { } connection ? await connection.ReadFileAsync(GamedataFiles.Absolute(_remote.Location) + "/" + item.Name, 4 << 20) : null;
            if (request != _previewRequest || bytes is null) return;
            var bitmap = new Bitmap(new MemoryStream(bytes));
            _preview.Source = bitmap;
            _previewText.Text = $"{info} · {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or Renci.SshNet.Common.SshException or NotSupportedException)
        {
            // Not a readable image, so only the text is shown.
        }
    }

    private async Task KeyAsync(Pane pane, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        switch (e.Key)
        {
            case Key.Tab: e.Handled = true; Activate(Other); _active.FocusList(); break;
            case Key.Enter: e.Handled = true; await pane.OpenSelectedAsync(); break;
            case Key.Back: e.Handled = true; await pane.UpAsync(); break;
            case Key.F5: e.Handled = true; await CopyAsync(); break;
            case Key.F2 or Key.F6: e.Handled = true; await RenameAsync(); break;
            case Key.F7: e.Handled = true; await NewFolderAsync(); break;
            case Key.F8 or Key.Delete: e.Handled = true; await DeleteAsync(); break;
            case Key.R when ctrl: e.Handled = true; await RefreshAsync(); break;
            case Key.A when ctrl: e.Handled = true; pane.SelectAll(); break;
        }
    }

    private sealed record Item(string Name, bool IsDirectory, long Size, DateTime Modified, string FullPath = "", bool IsParent = false, bool IsDrive = false);

    private sealed class Pane : Border
    {
        private readonly AssetExplorerView _owner;
        private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
        private readonly TextBlock _path = new() { FontFamily = Mono, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _summary = Text("", 11.5, "Pro.Subtle");

        public bool IsLocal { get; }
        /// <summary>An absolute folder or "" for the drive list on the local pane, a path relative to gamedata on the server pane.</summary>
        public string Location { get; set; } = "";
        public List<Item> Items { get; private set; } = [];

        public Pane(AssetExplorerView owner, bool local)
        {
            _owner = owner;
            IsLocal = local;
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(2);
            Padding = new Thickness(0, 8, 0, 6);
            this.Bind(BackgroundProperty, "Pro.Card");

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 4 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 12, 0) };
            header.Children.Add(Icon(local ? Icons.Home : Icons.Server, 16));
            header.Children.Add(Text(local ? "This computer" : "Server · gamedata (locked)", 13, weight: FontWeight.SemiBold));
            grid.Children.Add(header);
            _path.Margin = new Thickness(12, 0, 12, 2);
            _path.Bind(TextBlock.ForegroundProperty, "Pro.Muted");
            Grid.SetRow(_path, 1);
            grid.Children.Add(_path);

            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,90,130"), Margin = new Thickness(14, 0, 22, 0) };
            columns.Children.Add(Column("Name", 1));
            columns.Children.Add(Column("Size", 2, HorizontalAlignment.Right));
            columns.Children.Add(Column("Modified", 3, HorizontalAlignment.Right));
            var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            listPanel.Children.Add(columns);
            _list.ItemTemplate = new FuncDataTemplate<Item>((item, _) => Row(item));
            _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(12, 3)), new Setter(ListBoxItem.MinHeightProperty, 0d) }
            });
            _list.Background = Brushes.Transparent;
            Grid.SetRow(_list, 1);
            listPanel.Children.Add(_list);
            Grid.SetRow(listPanel, 2);
            grid.Children.Add(listPanel);
            _summary.Margin = new Thickness(12, 2, 12, 0);
            Grid.SetRow(_summary, 3);
            grid.Children.Add(_summary);
            Child = grid;

            _list.GotFocus += (_, _) => _owner.Activate(this);
            PointerPressed += (_, _) => _owner.Activate(this);
            _list.DoubleTapped += async (_, _) => await OpenSelectedAsync();
            _list.AddHandler(KeyDownEvent, async (_, e) => await _owner.KeyAsync(this, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            // Rows drag to the other pane, and local files can also be dragged out to the desktop. The server pane
            // also accepts files from Explorer. A drop on a folder row goes into that folder.
            DragDrop.SetAllowDrop(_list, true);
            _list.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.AddHandler(PointerMovedEvent, OnMoved, handledEventsToo: true);
            _list.AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            _list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropTarget(false));
            _list.AddHandler(DragDrop.DropEvent, async (_, e) => await OnDropAsync(e));
            _list.SelectionChanged += async (_, _) =>
            {
                UpdateSummary();
                await _owner.PreviewAsync(this);
            };
        }

        // Marks a drag that started in a pane as "local" or "remote". The dragged rows are that pane's selection.
        private static readonly DataFormat<string> PaneFormat = DataFormat.CreateInProcessFormat<string>("all-in-1-asset-explorer-pane");

        private PointerPressedEventArgs? _press;
        private Point _pressAt;
        // A plain press inside a multiple selection keeps it for a drag, and selects only that row if no drag follows.
        private Item? _collapseTo;

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            _press = null;
            if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            // The ".." and drive rows cannot be dragged.
            if (RowAt(e.Source) is not { IsParent: false, IsDrive: false } row) return;
            _press = e;
            _pressAt = e.GetPosition(_list);
            _collapseTo = null;
            if (e.KeyModifiers == KeyModifiers.None && (_list.SelectedItems?.Count ?? 0) > 1 && _list.SelectedItems!.Contains(row))
            {
                _collapseTo = row;
                e.Handled = true; // Otherwise the ListBox selects only this row and the drag loses the others.
                _owner.Activate(this);
            }
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_press is not null && _collapseTo is { } row)
            {
                _list.SelectedItems?.Clear();
                _list.SelectedItem = row;
            }
            _press = null;
            _collapseTo = null;
        }

        private async void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_press is not { } press || _owner._busy) return;
            Point here = e.GetPosition(_list);
            if (Math.Abs(here.X - _pressAt.X) < 6 && Math.Abs(here.Y - _pressAt.Y) < 6) return;
            _press = null;
            _collapseTo = null;
            List<Item> items = SelectedItems().Where(i => !i.IsDrive).ToList();
            if (items.Count == 0) return;

            var data = new DataTransfer();
            var marker = new DataTransferItem();
            marker.Set(PaneFormat, IsLocal ? "local" : "remote");
            data.Add(marker);
            // Local items are also offered as files so Explorer or the desktop can take a copy.
            if (IsLocal && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
                foreach (Item item in items)
                {
                    Avalonia.Platform.Storage.IStorageItem? storageItem = item.IsDirectory
                        ? await storage.TryGetFolderFromPathAsync(item.FullPath)
                        : await storage.TryGetFileFromPathAsync(item.FullPath);
                    if (storageItem is not null) data.Add(DataTransferItem.CreateFile(storageItem));
                }
            _owner.ShowStatus(IsLocal
                ? $"Drag {items.Count} item(s) to the server pane to upload (or onto a folder there)."
                : $"Drag {items.Count} item(s) to the left pane to download.");
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }

        private static Item? RowAt(object? source)
        {
            for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
                if (visual is ListBoxItem { DataContext: Item item }) return item;
            return null;
        }

        // A drop goes into the folder row under the pointer, or else into the folder on screen.
        private string? DropFolder(object? source)
        {
            Item? row = RowAt(source);
            if (IsLocal)
            {
                if (Location.Length == 0) return row is { IsDrive: true } ? row.FullPath : null;
                return row is { IsDirectory: true, IsParent: false } ? row.FullPath : Location;
            }
            return row is { IsDirectory: true, IsParent: false } ? (Location.Length == 0 ? row.Name : Location + "/" + row.Name) : Location;
        }

        private string? DragSource(DragEventArgs e)
        {
            if (e.DataTransfer.TryGetValue(PaneFormat) is { } pane) return pane;
            return e.DataTransfer.Contains(DataFormat.File) ? "files" : null;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            string? source = DragSource(e);
            // The server pane accepts the left pane and Explorer. The left pane only accepts the server pane.
            bool accepts = !_owner._busy && (IsLocal ? source == "remote" : source is "local" or "files") && DropFolder(e.Source) is not null;
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            ShowDropTarget(accepts);
            if (accepts && DropFolder(e.Source) is { } folder)
                _owner.ShowStatus(IsLocal ? $"Drop to download into {folder}" : $"Drop to upload into {GamedataFiles.Absolute(folder)}");
            e.Handled = true;
        }

        private void ShowDropTarget(bool on) => this.Bind(BackgroundProperty, on ? "Pro.AccentSoft" : "Pro.Card");

        private async Task OnDropAsync(DragEventArgs e)
        {
            ShowDropTarget(false);
            string? source = DragSource(e);
            string? folder = DropFolder(e.Source);
            e.Handled = true;
            if (folder is null || _owner._busy) return;
            if (!IsLocal && source == "local")
                await _owner.UploadAsync(_owner._local.SelectedItems().Where(i => !i.IsDrive).Select(i => i.FullPath).ToList(), folder);
            else if (!IsLocal && source == "files")
            {
                var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
                    .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (paths.Count == 0) { _owner.ShowStatus("Only files and folders on this computer can be dropped here.", error: true); return; }
                await _owner.UploadAsync(paths, folder);
            }
            else if (IsLocal && source == "remote")
                await _owner.DownloadAsync(_owner._remote.SelectedItems().Select(i => i.Name).ToList(), _owner._remote.Location, folder);
        }

        private static TextBlock Column(string text, int column, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            TextBlock block = Text(text, 11, "Pro.Subtle", FontWeight.SemiBold);
            block.HorizontalAlignment = align;
            Grid.SetColumn(block, column);
            return block;
        }

        private static Control Row(Item? item)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,90,130") };
            if (item is null) return row;
            string icon = item.IsParent ? Icons.ArrowUp : item.IsDrive ? Icons.Database : item.IsDirectory ? Icons.Folder
                : ImageExtensions.Contains(Path.GetExtension(item.Name).ToLowerInvariant()) ? Icons.Image : Icons.File;
            PathIcon glyph = Icon(icon, 15);
            glyph.Bind(PathIcon.ForegroundProperty, item.IsDirectory || item.IsParent || item.IsDrive ? "Pro.AccentText" : "Pro.Muted");
            row.Children.Add(glyph);
            TextBlock name = Text(item.IsParent ? ".." : item.Name, 12.5, weight: item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (!item.IsParent)
            {
                TextBlock size = Text(item.IsDrive ? (item.Size > 0 ? ServerStatusView.Bytes(item.Size) + " free" : "") : item.IsDirectory ? "<DIR>" : ServerStatusView.Bytes(item.Size), 12, "Pro.Muted");
                size.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(size, 2);
                row.Children.Add(size);
                if (!item.IsDrive)
                {
                    TextBlock date = Text(item.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), 12, "Pro.Muted");
                    date.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(date, 3);
                    row.Children.Add(date);
                }
            }
            return row;
        }

        public void SetActive(bool active) => this.Bind(BorderBrushProperty, active ? "Pro.AccentText" : "Pro.CardBorder");

        // Focus the selected row itself. The ListBox does not take focus, and after a reload the old row is gone, so
        // the arrow keys would move focus out to the sidebar instead of through the list.
        public void FocusList() => Dispatcher.UIThread.Post(() =>
        {
            if (_owner._active != this) return;
            if (_list.SelectedIndex < 0 && Items.Count > 0) _list.SelectedIndex = 0;
            int index = Math.Max(0, _list.SelectedIndex);
            if (Items.Count > 0) _list.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() =>
            {
                if (_list.ContainerFromIndex(index) is Control row) row.Focus(NavigationMethod.Directional);
                else _list.Focus();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);

        public void SelectAll() => _list.SelectAll();

        public List<Item> SelectedItems() => _list.SelectedItems?.OfType<Item>().Where(i => !i.IsParent).ToList() ?? [];

        public async Task LoadAsync(IEnumerable<string>? select = null)
        {
            List<Item> items = IsLocal ? LoadLocal() : await LoadRemoteAsync();
            Items = items;
            // Items are records with value equality, so the old selection would carry over to the new list.
            _list.SelectedItems?.Clear();
            _list.ItemsSource = items;
            _path.Text = IsLocal ? (Location.Length == 0 ? "Drives" : Location) : GamedataFiles.Absolute(Location);
            HashSet<string> wanted = select is null ? [] : [.. select];
            if (wanted.Count > 0)
                foreach (Item item in items.Where(i => wanted.Contains(i.Name))) _list.SelectedItems?.Add(item);
            else if (items.Count > 0) _list.SelectedIndex = 0;
            UpdateSummary();
        }

        private List<Item> LoadLocal()
        {
            if (Location.Length == 0)
            {
                // The drives on Windows, or / elsewhere, plus the home folder.
                var roots = new List<Item>();
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady || (!OperatingSystem.IsWindows() && drive.Name != "/")) continue;
                        string label = OperatingSystem.IsWindows() ? $"{drive.Name.TrimEnd('\\')}  {drive.VolumeLabel}".Trim() : "/";
                        roots.Add(new Item(label, true, drive.AvailableFreeSpace, default, drive.RootDirectory.FullName, IsDrive: true));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (home.Length > 0) roots.Add(new Item("Home  " + home, true, 0, default, home, IsDrive: true));
                return roots;
            }
            var items = new List<Item> { new("..", true, 0, default, IsParent: true) };
            var directory = new DirectoryInfo(Location);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            items.AddRange(directory.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Item(d.Name, true, 0, d.LastWriteTime, d.FullName)));
            items.AddRange(directory.EnumerateFiles("*", options).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Item(f.Name, false, f.Length, f.LastWriteTime, f.FullName)));
            return items;
        }

        private async Task<List<Item>> LoadRemoteAsync()
        {
            if (_owner._connection() is not { } connection) return [];
            List<RemoteEntry> entries = await Task.Run(() => GamedataFiles.ListAsync(connection, Location));
            var items = new List<Item>();
            // No ".." at the gamedata root, since the pane is locked to it.
            if (Location.Length > 0) items.Add(new Item("..", true, 0, default, IsParent: true));
            items.AddRange(entries.Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Item(e.Name, true, 0, e.Modified)));
            items.AddRange(entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Item(e.Name, false, e.Size, e.Modified)));
            return items;
        }

        private void UpdateSummary()
        {
            int folders = Items.Count(i => i.IsDirectory && !i.IsParent && !i.IsDrive), files = Items.Count(i => !i.IsDirectory);
            List<Item> selected = SelectedItems();
            _summary.Text = $"{folders} folders, {files} files ({ServerStatusView.Bytes(Items.Where(i => !i.IsDirectory).Sum(i => i.Size))})" +
                            (selected.Count > 1 ? $" · {selected.Count} selected" : "");
        }

        public async Task OpenSelectedAsync()
        {
            if (_owner._busy || _list.SelectedItem is not Item item) return;
            if (item.IsParent) { await UpAsync(); return; }
            if (!item.IsDirectory) return;
            string previous = Location;
            Location = IsLocal ? item.FullPath : (Location.Length == 0 ? item.Name : Location + "/" + item.Name);
            await TryLoadAsync(previous);
        }

        public async Task UpAsync()
        {
            if (_owner._busy) return;
            string previous = Location;
            string child;
            if (IsLocal)
            {
                if (Location.Length == 0) return;
                child = Path.GetFileName(Path.TrimEndingDirectorySeparator(Location));
                Location = Directory.GetParent(Path.TrimEndingDirectorySeparator(Location))?.FullName ?? "";
                if (Location.Length == 0) child = "";
            }
            else
            {
                if (Location.Length == 0) return; // locked to gamedata
                int slash = Location.LastIndexOf('/');
                child = Location[(slash + 1)..];
                Location = slash < 0 ? "" : Location[..slash];
            }
            await TryLoadAsync(previous, child.Length > 0 ? [child] : null);
        }

        private async Task TryLoadAsync(string previous, IEnumerable<string>? select = null)
        {
            try
            {
                await LoadAsync(select);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ServerConnectionException or ArgumentException)
            {
                Location = previous;
                _owner.ShowStatus("Cannot open that folder: " + ex.Message, error: true);
            }
            FocusList();
        }
    }
}
