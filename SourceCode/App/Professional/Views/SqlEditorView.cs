using System.Collections;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using MySqlConnector;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// A database client in the style of HeidiSQL: databases and tables on the left; the data of a table (editable when it
/// has a primary key), its structure and a query editor on the right. MariaDB is reached through the SSH session.
/// </summary>
public sealed class SqlEditorView : Grid
{
    private const int PageSize = 1000;
    private const int MaxQueryRows = 10000;
    private const string NullText = "(NULL)";

    private readonly Func<ServerConnection?> _connection;
    private readonly Func<Task> _disconnect;
    private SqlSession? _session;
    private ServerConnection? _sessionFor;
    private bool _opening;

    private readonly TextBlock _title = Text("", 13, weight: FontWeight.SemiBold);
    private readonly TextBlock _subtitle = Text("", 12, "Pro.Muted");
    private readonly Button _login = new() { Content = "Log in to MariaDB", Padding = new Thickness(14, 7), Classes = { "primary" }, IsVisible = false };
    private readonly TreeView _tree = new() { SelectionMode = SelectionMode.Single };
    private readonly TabControl _tabs = new();
    private readonly TabItem _dataTab = new() { Header = "Data", FontSize = 16 };
    private readonly TabItem _structureTab = new() { Header = "Structure", FontSize = 16 };
    private readonly TabItem _queryTab = new() { Header = "Query", FontSize = 16 };

    // Data tab
    private readonly ContentControl _dataHost = new();
    private readonly TextBox _filter = new() { PlaceholderText = "Filter: a WHERE condition, such as id > 100 AND username LIKE 'a%'", FontFamily = Mono, FontSize = 12.5 };
    private readonly TextBlock _dataStatus = Text("Select a table on the left.", 12, "Pro.Muted");
    private readonly Button _previous = SmallButton("◀ Previous");
    private readonly Button _next = SmallButton("Next ▶");
    private readonly List<Button> _dataButtons = [];
    private (string Database, string Table, bool IsView)? _table;
    private List<string> _primaryKey = [];
    private SqlResult? _data;
    private DataGrid? _dataGrid;
    private long _offset;
    private string? _orderBy;
    private bool _descending;
    private bool _dataBusy;

    // Structure tab
    private readonly ContentControl _columnsHost = new();
    private readonly TextBox _create = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };

    // Query tab
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        FontFamily = Mono,
        FontSize = 13,
        TextWrapping = TextWrapping.NoWrap,
        PlaceholderText = "SELECT * FROM users ORDER BY id DESC LIMIT 50;",
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly ComboBox _database = new() { MinWidth = 180, FontSize = 12.5, PlaceholderText = "(no database)" };
    private readonly TabControl _results = new();
    private readonly TextBlock _queryStatus = Text("F9 or Ctrl+Enter runs the selected text, or everything when nothing is selected.", 12, "Pro.Muted");
    private readonly Button _run = SmallButton("▶ Run (F9)", primary: true);
    private readonly Button _stop = SmallButton("■ Stop");
    private CancellationTokenSource? _queryCancel;

    public SqlEditorView(Func<ServerConnection?> connection, Func<Task> disconnect)
    {
        _connection = connection;
        _disconnect = disconnect;
        RowDefinitions = new RowDefinitions("Auto,*");
        RowSpacing = 10;
        Margin = new Thickness(32, 0, 32, 18);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _subtitle.TextWrapping = TextWrapping.Wrap;
        heading.Children.Add(_title);
        heading.Children.Add(_subtitle);
        bar.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _login.Click += async (_, _) => await OpenSessionAsync(askFirst: true);
        buttons.Children.Add(_login);
        Button refresh = SmallButton("Refresh");
        refresh.Click += async (_, _) => await LoadDatabasesAsync();
        buttons.Children.Add(refresh);
        var disconnectButton = new Button { Content = "Disconnect", Padding = new Thickness(14, 7), Classes = { "secondary" } };
        disconnectButton.Click += async (_, _) =>
        {
            if (_queryCancel is not null) return;
            await CloseSessionAsync();
            await _disconnect();
        };
        buttons.Children.Add(disconnectButton);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        Children.Add(bar);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,6,*") };
        body.Children.Add(Frame(new ScrollViewer { Content = _tree, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        _dataTab.Content = BuildDataTab();
        _structureTab.Content = BuildStructureTab();
        _queryTab.Content = BuildQueryTab();
        _tabs.ItemsSource = new[] { _dataTab, _structureTab, _queryTab };
        _tabs.SelectedItem = _queryTab;
        _tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == _tabs && _tabs.SelectedItem == _structureTab) await LoadStructureAsync();
        };
        var tabsFrame = Frame(_tabs);
        Grid.SetColumn(tabsFrame, 2);
        body.Children.Add(tabsFrame);
        Grid.SetRow(body, 1);
        Children.Add(body);

        _tree.SelectionChanged += async (_, _) => await TreeSelectionChangedAsync();
        AttachedToVisualTree += async (_, _) => await ConnectedAsync();
    }

    private static Border Frame(Control child) =>
        new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = child }
            .Bind(Border.BackgroundProperty, "Pro.Card").Bind(Border.BorderBrushProperty, "Pro.CardBorder");

    private static Button SmallButton(string text, bool primary = false) =>
        new() { Content = text, Padding = new Thickness(12, 6), FontSize = 12.5, Classes = { primary ? "primary" : "secondary" } };

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private static string Message(Exception ex) => ex is MySqlException or ServerConnectionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";

    private static void ShowStatus(TextBlock block, string text, bool error = false)
    {
        block.Text = text;
        block.Bind(TextBlock.ForegroundProperty, error ? "Pro.ErrorText" : "Pro.Muted");
    }

    // ---- Session ------------------------------------------------------------------------------------------------

    private async Task ConnectedAsync()
    {
        ServerConnection? connection = _connection();
        if (connection is null || connection == _sessionFor) return;
        await CloseSessionAsync();
        _sessionFor = connection;
        _title.Text = $"MariaDB on {connection.Login.Host} (through SSH as {connection.Login.User})";
        await OpenSessionAsync(askFirst: false);
    }

    /// <summary>Closes the database connections and the tunnel. The SSH login stays.</summary>
    public async Task CloseSessionAsync()
    {
        _queryCancel?.Cancel();
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
        _sessionFor = null;
        _tree.ItemsSource = null;
        _database.ItemsSource = null;
        _table = null;
        _data = null;
        _dataHost.Content = null;
        _columnsHost.Content = null;
        _create.Text = "";
        _results.ItemsSource = null;
        ShowStatus(_dataStatus, "Select a table on the left.");
    }

    // Logs in with the saved MariaDB root account of this server, or asks for an account.
    private async Task OpenSessionAsync(bool askFirst)
    {
        if (_opening || _connection() is not { } connection || Owner is not { } owner) return;
        _opening = true;
        try
        {
            if (_session is { } old)
            {
                _session = null;
                await old.DisposeAsync();
            }
            DatabaseCredentials? saved = ClientConfiguration.LoadDatabaseCredentials(connection.Login.Host);
            var login = new DatabaseLoginDialog.Login(saved?.RootUser ?? "root", saved?.RootPassword ?? "", saved?.Port ?? 3306);
            string? problem = saved is null ? "No saved MariaDB password for this server on this computer: enter an account." : null;
            bool ask = askFirst || saved is null;
            while (true)
            {
                if (ask)
                {
                    DatabaseLoginDialog.Login? entered = await DatabaseLoginDialog.ShowAsync(owner, connection.Login.Host, login, problem);
                    if (entered is null)
                    {
                        _login.IsVisible = true;
                        _subtitle.Text = "Not logged in to MariaDB. Click Log in to MariaDB.";
                        return;
                    }
                    login = entered;
                }
                _subtitle.Text = $"Connecting to MariaDB as {login.User} ...";
                try
                {
                    _session = await Task.Run(() => SqlSession.OpenAsync(connection, login.User, login.Password, login.Port));
                    break;
                }
                catch (Exception ex) when (ex is MySqlException or Renci.SshNet.Common.SshException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    problem = ex is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied }
                        ? $"MariaDB refused {login.User}: {ex.Message}"
                        : $"Could not reach MariaDB on 127.0.0.1:{login.Port} of the server: {ex.Message}";
                    ask = true;
                }
            }
            _login.IsVisible = false;
            _subtitle.Text = $"{_session.User}@127.0.0.1 · {_session.ServerVersion} · through the SSH tunnel; nothing is opened to the internet.";
            await LoadDatabasesAsync(saved?.Database);
        }
        finally
        {
            _opening = false;
        }
    }

    // ---- Database tree ------------------------------------------------------------------------------------------

    private sealed record TableNode(string Database, SqlTableInfo Table);
    private sealed class DatabaseNode(string name)
    {
        public string Name { get; } = name;
        public bool Loaded { get; set; }
    }

    private async Task LoadDatabasesAsync(string? select = null)
    {
        if (_session is not { } session) return;
        try
        {
            List<string> databases = await Task.Run(() => session.DatabasesAsync());
            var items = new List<TreeViewItem>();
            foreach (string database in databases)
            {
                var node = new DatabaseNode(database);
                var item = new TreeViewItem { Header = TreeHeader(Icons.Database, database, null), Tag = node, ItemsSource = new[] { new TreeViewItem { Header = "…" } } };
                item.PropertyChanged += async (_, e) =>
                {
                    if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !node.Loaded) await LoadTablesAsync(item, node);
                };
                items.Add(item);
            }
            _tree.ItemsSource = items;
            string? current = _database.SelectedItem as string;
            _database.ItemsSource = databases;
            _database.SelectedItem = databases.Contains(current ?? "") ? current : databases.Contains(select ?? "") ? select : null;
            if (_database.SelectedItem is string chosen && items.FirstOrDefault(i => ((DatabaseNode)i.Tag!).Name == chosen) is { } chosenItem)
                chosenItem.IsExpanded = true;
        }
        catch (Exception ex)
        {
            ShowStatus(_queryStatus, $"Loading the databases failed: {Message(ex)}", error: true);
        }
    }

    private async Task LoadTablesAsync(TreeViewItem item, DatabaseNode node)
    {
        if (_session is not { } session) return;
        string database = node.Name;
        node.Loaded = true;
        try
        {
            List<SqlTableInfo> tables = await Task.Run(() => session.TablesAsync(database));
            item.ItemsSource = tables.Select(t => new TreeViewItem
            {
                Header = TreeHeader(t.IsView ? Icons.Swap : Icons.File, t.Name, t.IsView ? "view" : Size(t.Bytes)),
                Tag = new TableNode(database, t)
            }).ToList();
        }
        catch (Exception ex)
        {
            node.Loaded = false;
            ShowStatus(_queryStatus, $"Loading the tables of {database} failed: {Message(ex)}", error: true);
        }
    }

    private static Control TreeHeader(string icon, string name, string? note)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var glyph = Icon(icon, 13);
        glyph.Foreground = Solid(Brand);
        panel.Children.Add(glyph);
        panel.Children.Add(Text(name, 12.5));
        if (note is not null) panel.Children.Add(Text(note, 11, "Pro.Subtle"));
        return panel;
    }

    private static string? Size(long? bytes) => bytes switch
    {
        null => null,
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.0} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };

    private async Task TreeSelectionChangedAsync()
    {
        switch ((_tree.SelectedItem as TreeViewItem)?.Tag)
        {
            case TableNode node:
                if (_database.ItemsSource is IEnumerable<string> names && names.Contains(node.Database)) _database.SelectedItem = node.Database;
                await OpenTableAsync(node);
                break;
            case DatabaseNode database:
                _database.SelectedItem = database.Name;
                break;
        }
    }

    // ---- Data tab -----------------------------------------------------------------------------------------------

    private Control BuildDataTab()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        _filter.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _offset = 0;
            await LoadDataAsync();
        };
        toolbar.Children.Add(_filter);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button apply = SmallButton("Apply filter");
        apply.Click += async (_, _) => { _offset = 0; await LoadDataAsync(); };
        _previous.Click += async (_, _) => { _offset = Math.Max(0, _offset - PageSize); await LoadDataAsync(); };
        _next.Click += async (_, _) => { _offset += PageSize; await LoadDataAsync(); };
        Button reload = SmallButton("Refresh");
        reload.Click += async (_, _) => await LoadDataAsync();
        Button delete = SmallButton("Delete rows");
        delete.Click += async (_, _) => await DeleteSelectedRowsAsync();
        Button export = SmallButton("Export CSV");
        export.Click += async (_, _) => { if (_data is not null) await ExportCsvAsync(_data, _table?.Table ?? "data"); };
        foreach (Button button in new[] { apply, _previous, _next, reload, delete, export })
        {
            actions.Children.Add(button);
            _dataButtons.Add(button);
        }
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);
        Grid.SetRow(_dataHost, 1);
        page.Children.Add(_dataHost);
        _dataStatus.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_dataStatus, 2);
        page.Children.Add(_dataStatus);
        return page;
    }

    private async Task OpenTableAsync(TableNode node)
    {
        if (_session is not { } session) return;
        _table = (node.Database, node.Table.Name, node.Table.IsView);
        _offset = 0;
        _orderBy = null;
        _descending = false;
        _filter.Text = "";
        _columnsHost.Content = null;
        _create.Text = "";
        try
        {
            _primaryKey = node.Table.IsView ? [] : await Task.Run(() => session.PrimaryKeyAsync(node.Database, node.Table.Name));
        }
        catch (Exception ex)
        {
            _primaryKey = [];
            ShowStatus(_dataStatus, $"Reading the primary key failed: {Message(ex)}", error: true);
        }
        if (_tabs.SelectedItem == _structureTab) await LoadStructureAsync();
        else _tabs.SelectedItem = _dataTab;
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (_session is not { } session || _table is not { } table || _dataBusy) return;
        _dataBusy = true;
        foreach (Button button in _dataButtons) button.IsEnabled = false;
        ShowStatus(_dataStatus, $"Loading {table.Database}.{table.Table} ...");
        try
        {
            string filter = _filter.Text ?? "";
            SqlResult result = await Task.Run(() => session.TableDataAsync(table.Database, table.Table, filter, _orderBy, _descending, _offset, PageSize));
            _data = result;
            bool editable = _primaryKey.Count > 0 && _primaryKey.All(k => result.Columns!.Any(c => c.Name == k));
            _dataGrid = BuildGrid(result, editable, serverSort: true);
            _dataHost.Content = _dataGrid;
            string range = result.Rows.Count == 0 ? "No rows" : $"Rows {_offset + 1:N0}–{_offset + result.Rows.Count:N0}";
            string order = _orderBy is null ? "" : $" · sorted by {_orderBy}{(_descending ? " (descending)" : "")}";
            string edit = editable
                ? " · Double-click a cell to change it (Enter saves, Esc cancels); right-click for NULL, copy and delete."
                : table.IsView ? " · A view: read-only." : " · No primary key: read-only (change rows with a query).";
            ShowStatus(_dataStatus, $"{table.Database}.{table.Table}: {range}{order} · {result.Elapsed.TotalSeconds:0.000} s{edit}");
        }
        catch (Exception ex)
        {
            ShowStatus(_dataStatus, $"Loading {table.Table} failed: {Message(ex)}", error: true);
        }
        finally
        {
            _dataBusy = false;
            foreach (Button button in _dataButtons) button.IsEnabled = true;
            _previous.IsEnabled = _offset > 0;
            _next.IsEnabled = _data?.Rows.Count == PageSize;
        }
    }

    private IReadOnlyList<(string Column, object? Value)> KeyOf(GridRow row) =>
        _primaryKey.Select(k => (k, row.Values[_data!.Columns!.ToList().FindIndex(c => c.Name == k)])).ToList();

    private async Task SaveCellAsync(GridRow row, int column, string? text)
    {
        if (_session is not { } session || _table is not { } table || _data?.Columns is not { } columns) return;
        string name = columns[column].Name;
        var key = KeyOf(row);
        try
        {
            long changed = await Task.Run(() => session.UpdateCellAsync(table.Database, table.Table, name, text, key));
            row.Values[column] = text;
            ShowStatus(_dataStatus, changed > 0
                ? $"{name} set to {(text is null ? "NULL" : $"'{Shorten(text)}'")} in {table.Table} ({string.Join(", ", key.Select(k => $"{k.Column} = {SqlSession.Display(k.Value)}"))})."
                : "No row changed: the value was the same, or the row is gone (Refresh).");
        }
        catch (Exception ex)
        {
            ShowStatus(_dataStatus, $"Saving {name} failed: {Message(ex)}", error: true);
        }
        row.Changed();
    }

    private static string Shorten(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private async Task DeleteSelectedRowsAsync()
    {
        if (_session is not { } session || _table is not { } table || _dataGrid is null || Owner is not { } owner) return;
        var rows = _dataGrid.SelectedItems.OfType<GridRow>().ToList();
        if (rows.Count == 0) { ShowStatus(_dataStatus, "Select the rows to delete first (Ctrl/Shift+click selects more)."); return; }
        if (_primaryKey.Count == 0) { ShowStatus(_dataStatus, "This table has no primary key: delete rows with a query.", error: true); return; }
        bool go = await ChoiceDialog.ShowAsync(owner, "Are you sure?", $"Delete {rows.Count} row{(rows.Count == 1 ? "" : "s")}?",
            $"The selected rows are deleted from {table.Database}.{table.Table}. This cannot be undone (make a backup with Tools → Create full database backup).", false,
            new ChoiceDialog.Option<bool>("Delete", "Delete the rows.", true, Primary: true),
            new ChoiceDialog.Option<bool>("Cancel", "Change nothing.", false));
        if (!go) return;
        try
        {
            var keys = rows.Select(KeyOf).ToList();
            long deleted = await Task.Run(() => session.DeleteRowsAsync(table.Database, table.Table, keys));
            await LoadDataAsync();
            ShowStatus(_dataStatus, $"{deleted} row{(deleted == 1 ? "" : "s")} deleted from {table.Table}.");
        }
        catch (Exception ex)
        {
            ShowStatus(_dataStatus, $"Deleting failed: {Message(ex)}", error: true);
        }
    }

    // ---- Structure tab ------------------------------------------------------------------------------------------

    private Control BuildStructureTab()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("*,6,*"), Margin = new Thickness(0, 8, 0, 0) };
        page.Children.Add(_columnsHost);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(splitter, 1);
        page.Children.Add(splitter);
        Grid.SetRow(_create, 2);
        page.Children.Add(_create);
        return page;
    }

    private async Task LoadStructureAsync()
    {
        if (_session is not { } session || _table is not { } table || _columnsHost.Content is not null) return;
        try
        {
            SqlResult columns = await Task.Run(() => session.ColumnsAsync(table.Database, table.Table));
            _columnsHost.Content = BuildGrid(columns, editable: false, serverSort: false);
            _create.Text = await Task.Run(() => session.CreateStatementAsync(table.Database, table.Table));
        }
        catch (Exception ex)
        {
            _create.Text = $"-- Reading the structure of {table.Table} failed: {Message(ex)}";
        }
    }

    // ---- Query tab ----------------------------------------------------------------------------------------------

    private Control BuildQueryTab()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,2*,6,3*,Auto"), RowSpacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        _run.Click += async (_, _) => await RunQueryAsync();
        _stop.IsEnabled = false;
        _stop.Click += (_, _) => _queryCancel?.Cancel();
        Button open = SmallButton("Open .sql");
        open.Click += async (_, _) => await OpenScriptAsync();
        Button save = SmallButton("Save .sql");
        save.Click += async (_, _) => await SaveScriptAsync();
        Button export = SmallButton("Export CSV");
        export.Click += async (_, _) =>
        {
            if ((_results.SelectedItem as TabItem)?.Tag is SqlResult result) await ExportCsvAsync(result, "result");
        };
        toolbar.Children.Add(_run);
        toolbar.Children.Add(_stop);
        toolbar.Children.Add(Text("Database:", 12.5, "Pro.Muted"));
        ((TextBlock)toolbar.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_database);
        toolbar.Children.Add(open);
        toolbar.Children.Add(save);
        toolbar.Children.Add(export);
        page.Children.Add(toolbar);

        _editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F9 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                e.Handled = true;
                await RunQueryAsync();
            }
        };
        Grid.SetRow(_editor, 1);
        page.Children.Add(_editor);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(splitter, 2);
        page.Children.Add(splitter);
        Grid.SetRow(_results, 3);
        page.Children.Add(_results);
        _queryStatus.TextWrapping = TextWrapping.Wrap;
        _queryStatus.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_queryStatus, 4);
        page.Children.Add(_queryStatus);
        return page;
    }

    private async Task RunQueryAsync()
    {
        if (_session is not { } session || _queryCancel is not null) return;
        string script = _editor.SelectedText is { Length: > 0 } selected ? selected : _editor.Text ?? "";
        if (string.IsNullOrWhiteSpace(script)) { ShowStatus(_queryStatus, "Type a query first."); return; }
        string? database = _database.SelectedItem as string;
        _queryCancel = new CancellationTokenSource();
        CancellationToken token = _queryCancel.Token;
        _run.IsEnabled = false;
        _stop.IsEnabled = true;
        ShowStatus(_queryStatus, "Running ...");
        // A progress line posted just before the end must not replace the outcome.
        bool finished = false;
        try
        {
            SqlRun run = await Task.Run(() => session.RunScriptAsync(script, database, MaxQueryRows,
                (n, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!finished && total > 1) ShowStatus(_queryStatus, $"Running statement {n} of {total} ...");
                }),
                token), CancellationToken.None);
            finished = true;
            ShowResults(run);
            string? now = await Task.Run(() => session.CurrentDatabaseAsync());
            if (now != database && _database.ItemsSource is IEnumerable<string> names)
            {
                // CREATE DATABASE or DROP DATABASE changes the list.
                if (now is not null && !names.Contains(now)) await LoadDatabasesAsync(now);
                _database.SelectedItem = now;
            }
        }
        catch (Exception ex)
        {
            finished = true;
            ShowStatus(_queryStatus, $"The query failed: {Message(ex)}", error: true);
        }
        finally
        {
            _queryCancel.Dispose();
            _queryCancel = null;
            _run.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void ShowResults(SqlRun run)
    {
        var tabs = new List<TabItem>();
        var messages = new StringBuilder();
        int number = 0;
        long affected = 0;
        foreach (SqlResult result in run.Results)
        {
            if (result.Columns is null)
            {
                affected += result.Affected;
                continue;
            }
            number++;
            string rows = $"{result.Rows.Count:N0}{(result.Truncated ? "+" : "")} row{(result.Rows.Count == 1 ? "" : "s")}";
            tabs.Add(new TabItem { Header = $"Result {number} ({rows})", FontSize = 13.5, Tag = result, Content = BuildGrid(result, editable: false, serverSort: false) });
            if (result.Truncated) messages.AppendLine($"Result {number}: only the first {MaxQueryRows:N0} rows are shown; add a LIMIT to see others.");
        }
        int done = run.Results.Select(r => r.Statement).Distinct().Count();
        messages.Insert(0, $"{done} of {run.Statements} statement{(run.Statements == 1 ? "" : "s")} executed" +
                           (affected > 0 ? $", {affected:N0} row{(affected == 1 ? "" : "s")} affected" : "") +
                           (run.Results.Count > 0 ? $" · {run.Results.Max(r => r.Elapsed).TotalSeconds:0.000} s" : "") + ".\n");
        if (run.Error is not null) messages.AppendLine(run.Error);
        var log = new TextBox { Text = messages.ToString().TrimEnd(), IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tabs.Add(new TabItem { Header = run.Error is null ? "Messages" : "Messages ⚠", FontSize = 13.5, Content = log });
        _results.ItemsSource = tabs;
        _results.SelectedItem = run.Error is not null || number == 0 ? tabs[^1] : tabs[0];
        ShowStatus(_queryStatus, messages.ToString().Split('\n')[0] + (run.Error is null ? "" : "  " + run.Error), error: run.Error is not null && !run.Cancelled);
        if (run.Error is not null && !run.Cancelled) SelectLine(run.ErrorLine);
    }

    // Puts the caret on the line of the statement that failed (when the whole editor was run).
    private void SelectLine(int line)
    {
        if (_editor.SelectedText.Length > 0 || _editor.Text is not { } text || line < 1) return;
        int index = 0;
        for (int i = 1; i < line && index >= 0; i++) index = text.IndexOf('\n', index) is var next and >= 0 ? next + 1 : -1;
        if (index < 0) return;
        int end = text.IndexOf('\n', index);
        _editor.SelectionStart = index;
        _editor.SelectionEnd = end < 0 ? text.Length : end;
    }

    private async Task OpenScriptAsync()
    {
        if (Owner is not { } owner) return;
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an SQL file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }, new FilePickerFileType("All files") { Patterns = ["*"] }]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var info = new FileInfo(path);
        // Big dumps belong in Tools → Import full database; a text box of many megabytes is unusable.
        if (info.Length > 5 << 20)
        {
            ShowStatus(_queryStatus, $"{info.Name} is {Size(info.Length)}: import big dumps with Tools → Import full database.", error: true);
            return;
        }
        _editor.Text = await File.ReadAllTextAsync(path);
        _tabs.SelectedItem = _queryTab;
        ShowStatus(_queryStatus, $"Opened {info.Name}. F9 runs it.");
    }

    private async Task SaveScriptAsync()
    {
        if (Owner is not { } owner) return;
        IStorageFile? target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the query as",
            SuggestedFileName = "query.sql",
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL file") { Patterns = ["*.sql"] }]
        });
        if (target?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, _editor.Text ?? "");
        ShowStatus(_queryStatus, $"Saved {path}.");
    }

    private async Task ExportCsvAsync(SqlResult result, string name)
    {
        if (Owner is not { } owner || result.Columns is not { } columns) return;
        IStorageFile? target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export as CSV",
            SuggestedFileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }]
        });
        if (target?.TryGetLocalPath() is not { } path) return;
        static string Field(string? value) =>
            value is null ? "" : value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", columns.Select(c => Field(c.Name))));
        foreach (object?[] row in result.Rows)
            csv.AppendLine(string.Join(",", row.Select((v, i) => Field(SqlSession.Display(v, columns[i], int.MaxValue)))));
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        TextBlock status = _tabs.SelectedItem == _queryTab ? _queryStatus : _dataStatus;
        ShowStatus(status, $"{result.Rows.Count:N0} rows exported to {path}.");
    }

    // ---- Result grids -------------------------------------------------------------------------------------------

    /// <summary>One row of a grid. The DataGrid binds to the indexer; an edit is kept until the cell edit has ended.</summary>
    private sealed class GridRow(object?[] values, IReadOnlyList<SqlColumn> columns) : INotifyPropertyChanged
    {
        public object?[] Values { get; } = values;
        public (int Column, string? Text)? Edit { get; set; }

        public string this[int index]
        {
            get => SqlSession.Display(Values[index], columns[index]) ?? NullText;
            set
            {
                if (value != this[index]) Edit = (index, value);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }

    private sealed class ValueComparer(int index) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            object? a = (x as GridRow)?.Values[index], b = (y as GridRow)?.Values[index];
            if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
            if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
            if (IsNumber(a) && IsNumber(b)) return Convert.ToDecimal(a).CompareTo(Convert.ToDecimal(b));
            return string.CompareOrdinal(SqlSession.Display(a), SqlSession.Display(b));
        }

        private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private DataGrid BuildGrid(SqlResult result, bool editable, bool serverSort)
    {
        IReadOnlyList<SqlColumn> columns = result.Columns ?? [];
        var grid = new DataGrid
        {
            IsReadOnly = !editable,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            FontSize = 12.5
        };
        for (int i = 0; i < columns.Count; i++)
        {
            SqlColumn column = columns[i];
            var gridColumn = new DataGridTextColumn
            {
                Header = column.Name,
                Binding = new ReflectionBinding($"[{i}]") { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay },
                IsReadOnly = !editable || column.IsBinary,
                Tag = i,
                MaxWidth = 420,
                CustomSortComparer = new ValueComparer(i)
            };
            grid.Columns.Add(gridColumn);
        }
        grid.ItemsSource = result.Rows.Select(r => new GridRow(r, columns)).ToList();

        if (serverSort)
            grid.Sorting += async (_, e) =>
            {
                // Sort the whole table on the server, not just this page.
                e.Handled = true;
                string name = columns[(int)e.Column.Tag!].Name;
                _descending = _orderBy == name && !_descending;
                _orderBy = name;
                _offset = 0;
                await LoadDataAsync();
            };
        if (editable)
            grid.CellEditEnded += async (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not GridRow row || row.Edit is not { } edit) return;
                row.Edit = null;
                await SaveCellAsync(row, edit.Column, edit.Text);
            };
        grid.ContextMenu = BuildContextMenu(grid, columns, editable);
        return grid;
    }

    private ContextMenu BuildContextMenu(DataGrid grid, IReadOnlyList<SqlColumn> columns, bool editable)
    {
        var copy = new MenuItem { Header = "Copy value" };
        copy.Click += async (_, _) =>
        {
            if (grid.SelectedItem is GridRow row && grid.CurrentColumn?.Tag is int index && TopLevel.GetTopLevel(grid)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(SqlSession.Display(row.Values[index], columns[index], int.MaxValue) ?? "");
        };
        var copyRows = new MenuItem { Header = "Copy rows (with column names)" };
        copyRows.Click += async (_, _) =>
        {
            var rows = grid.SelectedItems.OfType<GridRow>().ToList();
            if (rows.Count == 0 || TopLevel.GetTopLevel(grid)?.Clipboard is not { } clipboard) return;
            var text = new StringBuilder(string.Join("\t", columns.Select(c => c.Name))).AppendLine();
            foreach (GridRow row in rows) text.AppendLine(string.Join("\t", row.Values.Select((v, i) => SqlSession.Display(v, columns[i], int.MaxValue) ?? "NULL")));
            await clipboard.SetTextAsync(text.ToString());
        };
        var menu = new ContextMenu { ItemsSource = new List<MenuItem> { copy, copyRows } };
        if (!editable) return menu;

        var setNull = new MenuItem { Header = "Set to NULL" };
        setNull.Click += async (_, _) =>
        {
            if (grid.SelectedItem is GridRow row && grid.CurrentColumn?.Tag is int index && row.Values[index] is not null)
                await SaveCellAsync(row, index, null);
        };
        var delete = new MenuItem { Header = "Delete selected rows" };
        delete.Click += async (_, _) => await DeleteSelectedRowsAsync();
        menu.ItemsSource = new List<MenuItem> { copy, copyRows, setNull, delete };
        return menu;
    }
}
