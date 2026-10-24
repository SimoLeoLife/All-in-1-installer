using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// Live server dashboard: CPU, memory, network graphs, disks, logged-in users, uptime and the git versions of
/// the hotel. Samples every two seconds while it is on screen.
/// </summary>
public sealed class ServerStatusView : StackPanel
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly Color CpuColor = Brand;
    private static readonly Color MemColor = Color.Parse("#8B5CF6");
    private static readonly Color InColor = Color.Parse("#0E9F9E");
    private static readonly Color OutColor = Color.Parse("#E8590C");

    private readonly Func<ServerConnection?> _connection;
    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private StatusSample? _previous;
    private bool _sampling;

    private readonly TextBlock _updated = Text("Connecting ...", 12, "Pro.Subtle");
    private readonly TextBlock _cpu = Big(), _cpuDetail = Small();
    private readonly TextBlock _mem = Big(), _memDetail = Small();
    private readonly TextBlock _netIn = Big(), _netOut = Small();
    private readonly TextBlock _uptime = Big(), _uptimeDetail = Small();
    private readonly LineChart _usageChart = new(CpuColor, MemColor) { Maximum = 100, Height = 150 };
    private readonly LineChart _netChart = new(InColor, OutColor) { Height = 150 };
    private readonly TextBlock _netScale = Text("", 11.5, "Pro.Subtle");
    private readonly StackPanel _disks = new() { Spacing = 10 };
    private readonly StackPanel _users = new() { Spacing = 6 };
    private readonly Grid _versions = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"), ColumnSpacing = 18, RowSpacing = 6 };

    public ServerStatusView(Func<ServerConnection?> connection)
    {
        _connection = connection;
        Spacing = 14;
        _usageChart.Bind(LineChart.GridBrushProperty, "Pro.CardBorder");
        _netChart.Bind(LineChart.GridBrushProperty, "Pro.CardBorder");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        // The page title says "Server status"; here only when it was last updated.
        _updated.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_updated, 1);
        header.Children.Add(_updated);
        Children.Add(header);

        var tiles = new UniformGrid { Columns = 4 };
        tiles.Children.Add(Tile("CPU", Icons.Cog, CpuColor, _cpu, _cpuDetail));
        tiles.Children.Add(Tile("Memory", Icons.Database, MemColor, _mem, _memDetail));
        tiles.Children.Add(Tile("Network in / out", Icons.Swap, InColor, _netIn, _netOut));
        tiles.Children.Add(Tile("Uptime", Icons.History, OutColor, _uptime, _uptimeDetail));
        Children.Add(tiles);

        var charts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        charts.Children.Add(Panel("CPU and memory (%)", Legend(("CPU", CpuColor), ("Memory", MemColor)), _usageChart));
        var net = Panel("Network", Legend(("In", InColor), ("Out", OutColor)), _netChart, _netScale);
        Grid.SetColumn(net, 1);
        charts.Children.Add(net);
        Children.Add(charts);

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        lists.Children.Add(Panel("Disks", null, _disks));
        var users = Panel("Logged in users", null, _users);
        Grid.SetColumn(users, 1);
        lists.Children.Add(users);
        Children.Add(lists);

        var refresh = new Button { Content = "Refresh", Padding = new Thickness(14, 6), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        refresh.Click += async (_, _) => await LoadVersionsAsync();
        Children.Add(Panel("Versions", refresh, _versions));

        _timer.Tick += async (_, _) => await SampleAsync();
        AttachedToVisualTree += async (_, _) => await StartAsync();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>Starts sampling again, e.g. after a new login; the graphs keep their history.</summary>
    public async Task StartAsync()
    {
        _previous = null;
        _timer.Start();
        await SampleAsync();
        await LoadVersionsAsync();
    }

    // ---------------------------------------------------------------- sampling

    private async Task SampleAsync()
    {
        if (_sampling) return;
        if (_connection() is not { } connection)
        {
            _timer.Stop();
            _updated.Text = "Not connected.";
            return;
        }
        _sampling = true;
        try
        {
            StatusSample sample = await Task.Run(() => ServerStatus.SampleAsync(connection));
            Show(sample);
            _updated.Text = $"Live, every {Interval.TotalSeconds:0} seconds · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _updated.Text = $"Stopped: {ex.Message}";
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Show(StatusSample s)
    {
        double memPercent = s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0;
        _mem.Text = $"{memPercent:0}%";
        _memDetail.Text = $"{Bytes(s.MemUsed)} of {Bytes(s.MemTotal)}" + (s.SwapTotal > 0 ? $" · swap {Bytes(s.SwapTotal - s.SwapFree)}" : "");
        _uptime.Text = Uptime(s.UptimeSeconds);
        _uptimeDetail.Text = (s.EmulatorRunning ? "Emulator running" : "Emulator not running") +
                             (s.PlayersOnline is { } players ? $" · {players} online" : "");

        if (_previous is { } previous)
        {
            double cpu = s.CpuPercent(previous);
            (double inRate, double outRate) = s.BytesPerSecond(previous);
            _cpu.Text = $"{cpu:0}%";
            _netIn.Text = $"↓ {Rate(inRate)}";
            _netOut.Text = $"↑ {Rate(outRate)}";
            _usageChart.Add(cpu, memPercent);
            _netChart.Add(inRate, outRate);
            _netScale.Text = $"Top of the graph: {Rate(_netChart.Top)} · last {_netChart.Capacity * Interval.TotalSeconds / 60:0} minutes";
        }
        else
        {
            _cpu.Text = "…";
            _netIn.Text = "↓ …";
            _netOut.Text = "↑ …";
        }
        _cpuDetail.Text = $"{s.Cores} cores · load {s.Load}";
        _previous = s;

        _disks.Children.Clear();
        foreach (DiskUsage disk in s.Disks)
        {
            var row = new StackPanel { Spacing = 3 };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            line.Children.Add(Text($"{disk.Mount}  ({disk.Device})", 12.5, weight: FontWeight.SemiBold));
            TextBlock size = Text($"{Bytes(disk.Used)} of {Bytes(disk.Total)} · {Bytes(disk.Available)} free", 12, "Pro.Muted");
            Grid.SetColumn(size, 1);
            line.Children.Add(size);
            row.Children.Add(line);
            row.Children.Add(Bar(disk.Percent));
            _disks.Children.Add(row);
        }
        if (s.Disks.Count == 0) _disks.Children.Add(Text("No disks found.", 12.5, "Pro.Muted"));

        _users.Children.Clear();
        foreach (LoggedInUser user in s.Users)
            _users.Children.Add(Text($"{user.User}   {user.Terminal}   since {user.Since}" + (user.From.Length > 0 ? $"   from {user.From}" : ""), 12.5));
        if (s.Users.Count == 0) _users.Children.Add(Text("Nobody is logged in on a terminal (this installer uses commands, not a terminal).", 12.5, "Pro.Muted"));
    }

    private async Task LoadVersionsAsync()
    {
        if (_connection() is not { } connection) return;
        IReadOnlyList<RepositoryVersion> versions;
        try
        {
            versions = await Task.Run(() => ServerStatus.VersionsAsync(connection));
        }
        catch (Exception ex)
        {
            _versions.Children.Clear();
            _versions.Children.Add(Text($"Could not read the versions: {ex.Message}", 12.5, "Pro.ErrorText"));
            return;
        }
        _versions.Children.Clear();
        _versions.RowDefinitions.Clear();
        string[] headings = ["", "Commit", "Version", "Date", "Last change"];
        AddRow(0, headings.Select(h => Text(h, 11.5, "Pro.Muted", FontWeight.SemiBold)).ToArray());
        int row = 1;
        foreach (RepositoryVersion v in versions)
        {
            AddRow(row++,
                Text(v.Name, 12.5, weight: FontWeight.SemiBold),
                Mono($"{v.Commit ?? "?"}" + (v.Branch is { } branch ? $" ({branch})" : "")),
                Text(v.Build ?? "", 12.5),
                Text(v.Date ?? "", 12.5, "Pro.Muted"),
                new TextBlock { Text = v.Subject ?? "", FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis }.Bind(TextBlock.ForegroundProperty, "Pro.Muted"));
        }
        if (versions.Count == 0) AddRow(1, Text("No hotel repositories found on this server.", 12.5, "Pro.Muted"));
    }

    private void AddRow(int row, params Control[] cells)
    {
        _versions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetRow(cells[i], row);
            Grid.SetColumn(cells[i], i);
            _versions.Children.Add(cells[i]);
        }
    }

    // ---------------------------------------------------------------- building blocks

    private static TextBlock Big() => Text("…", 24, weight: FontWeight.SemiBold);
    private static TextBlock Small() => Text("", 12, "Pro.Muted");

    private static SelectableTextBlock Mono(string text) =>
        new SelectableTextBlock { Text = text, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"), FontSize = 12.5 }
            .Bind(SelectableTextBlock.ForegroundProperty, "Pro.Text");

    private static Control Tile(string title, string icon, Color color, TextBlock value, TextBlock detail)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var glyph = Icon(icon, 15);
        glyph.Foreground = Solid(color);
        head.Children.Add(glyph);
        head.Children.Add(Text(title, 12, "Pro.Muted", FontWeight.SemiBold));
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(head);
        panel.Children.Add(value);
        detail.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(detail);
        return Box(panel, new Thickness(0, 0, 10, 0));
    }

    private static Control Panel(string title, Control? side, params Control[] content)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Text(title, 13, weight: FontWeight.SemiBold));
        if (side is not null)
        {
            Grid.SetColumn(side, 1);
            head.Children.Add(side);
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(head);
        foreach (Control c in content) panel.Children.Add(c);
        return Box(panel, default);
    }

    private static Border Box(Control child, Thickness margin) =>
        new Border
        {
            Child = child,
            Padding = new Thickness(14, 12),
            Margin = margin,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        }.Bind(Border.BorderBrushProperty, "Pro.CardBorder");

    private static Control Legend(params (string Name, Color Color)[] items)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach ((string name, Color color) in items)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Solid(color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(Text(name, 11.5, "Pro.Muted"));
            legend.Children.Add(item);
        }
        return legend;
    }

    private static Control Bar(double percent)
    {
        Color color = percent >= 90 ? Color.Parse("#DC2626") : percent >= 75 ? Color.Parse("#D97706") : Color.Parse("#16A34A");
        var track = new Grid { Height = 8, ColumnDefinitions = new ColumnDefinitions($"{Math.Max(percent, 0.5):0.##}*,{Math.Max(100 - percent, 0.5):0.##}*") };
        track.Children.Add(new Border { Background = Solid(color), CornerRadius = new CornerRadius(4, 0, 0, 4) });
        var outer = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = track };
        outer.Bind(Border.BackgroundProperty, "Pro.Hover");
        return outer;
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>B/s, KB/s or MB/s, whichever reads best.</summary>
    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.00} MB/s"
        : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0.0} KB/s"
        : $"{bytesPerSecond:0} B/s";

    private static string Bytes(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} TB"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : $"{bytes / 1024.0:0} KB";

    private static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";
    }
}
