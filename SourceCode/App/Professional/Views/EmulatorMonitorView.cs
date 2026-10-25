using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;
using static AllInOneInstaller.App.Professional.Views.ServerStatusView;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// Live view of the emulator's CPU, memory and wired threads, plus the rooms from the log's slow-wired warnings.
/// Samples only while it is on screen.
/// </summary>
public sealed class EmulatorMonitorView : StackPanel
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int LogEvery = 5;
    private static readonly Color EmuColor = Brand;
    private static readonly Color WiredColor = Color.Parse("#E8590C");

    private readonly Func<ServerConnection?> _connection;
    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private EmulatorSample? _previous;
    // Full thread names from a JVM thread dump, re-read when unknown threads appear.
    private Dictionary<int, string> _names = [];
    private int _namesPid;
    private bool _sampling;
    private int _ticks;

    private readonly TextBlock _updated = Text("Connecting ...", 12, "Pro.Subtle");
    private readonly TextBlock _cpu = Big(), _cpuDetail = Small();
    private readonly TextBlock _wired = Big(), _wiredDetail = Small();
    private readonly TextBlock _mem = Big(), _memDetail = Small();
    private readonly TextBlock _threads = Big(), _threadsDetail = Small();
    private readonly LineChart _chart = new(EmuColor, WiredColor) { Height = 160 };
    private readonly TextBlock _chartScale = Text("", 11.5, "Pro.Subtle");
    private readonly Grid _wiredThreads = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 7 };
    private readonly Grid _groups = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 7 };
    private readonly TextBlock _logSummary = Text("", 12.5, "Pro.Muted");
    private readonly Grid _rooms = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 16, RowSpacing = 6 };
    private readonly StackPanel _recent = new() { Spacing = 3 };

    public EmulatorMonitorView(Func<ServerConnection?> connection)
    {
        _connection = connection;
        Spacing = 14;
        _chart.Bind(LineChart.GridBrushProperty, "Pro.CardBorder");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Text("Read from the emulator's threads in /proc and from its log; the emulator itself is not changed.", 12, "Pro.Subtle"));
        Grid.SetColumn(_updated, 1);
        header.Children.Add(_updated);
        Children.Add(header);

        var tiles = new UniformGrid { Columns = 4 };
        tiles.Children.Add(Tile("Emulator CPU", Icons.Server, EmuColor, _cpu, _cpuDetail));
        tiles.Children.Add(Tile("Wired CPU", Icons.Cog, WiredColor, _wired, _wiredDetail));
        tiles.Children.Add(Tile("Memory (JVM)", Icons.Database, Color.Parse("#8B5CF6"), _mem, _memDetail));
        tiles.Children.Add(Tile("Threads", Icons.History, Color.Parse("#0E9F9E"), _threads, _threadsDetail));
        Children.Add(tiles);

        Children.Add(Panel("CPU (% of one core)", Legend(("Emulator", EmuColor), ("Wired", WiredColor)), _chart, _chartScale));

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        lists.Children.Add(Panel("Wired threads", null,
            Text("The wired engine: a coordinator every 50 ms, shards that run the rooms' wired, and a worker for heavy rooms.", 11.5, "Pro.Subtle"),
            _wiredThreads));
        var groups = Panel("Where the emulator's CPU goes", null, _groups);
        Grid.SetColumn(groups, 1);
        lists.Children.Add(groups);
        Children.Add(lists);

        var refresh = new Button { Content = "Read the log now", Padding = new Thickness(14, 6), Classes = { "secondary" } };
        refresh.Click += async (_, _) => await ReadLogAsync();
        Children.Add(Panel("Rooms with slow wired (emulator log, since the last start)", refresh, _logSummary, _rooms, _recent));

        _timer.Tick += async (_, _) => await SampleAsync();
        AttachedToVisualTree += async (_, _) => await StartAsync();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public async Task StartAsync()
    {
        _previous = null;
        _ticks = 0;
        _timer.Start();
        await SampleAsync();
    }

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
            EmulatorSample sample = await Task.Run(() => EmulatorMonitor.SampleAsync(connection));
            if (sample.Running && (sample.Pid != _namesPid || sample.Threads.Any(t => !_names.ContainsKey(t.Id))))
            {
                _names = await Task.Run(() => EmulatorMonitor.ThreadNamesAsync(connection, sample.Pid));
                _namesPid = sample.Pid;
                // A thread dump briefly pauses the JVM. Threads it did not list keep their /proc name so they
                // don't trigger another dump.
                foreach (JvmThread thread in sample.Threads) _names.TryAdd(thread.Id, thread.Name);
            }
            Show(sample with { Threads = sample.Threads.Select(t => _names.TryGetValue(t.Id, out string? name) ? t with { Name = name } : t).ToList() });
            if (_ticks++ % LogEvery == 0)
            {
                // A log problem must not stop the live figures.
                try { await ReadLogAsync(connection); }
                catch (Exception ex) { _logSummary.Text = $"Could not read the log: {ex.Message}"; }
            }
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

    private void Show(EmulatorSample s)
    {
        if (!s.Running)
        {
            _cpu.Text = _wired.Text = _mem.Text = _threads.Text = "–";
            _cpuDetail.Text = "The emulator is not running.";
            _wiredDetail.Text = _memDetail.Text = _threadsDetail.Text = "";
            _wiredThreads.Children.Clear();
            _groups.Children.Clear();
            _previous = null;
            return;
        }

        _mem.Text = Bytes(s.RssBytes);
        _memDetail.Text = s.MaxHeapBytes > 0 ? $"in use · heap limit (-Xmx) {Bytes(s.MaxHeapBytes)}" : "in use";
        _threads.Text = s.Threads.Count.ToString();
        _threadsDetail.Text = $"running {s.Threads.Count(t => t.State == 'R')} · up {Uptime(s.StartedSecondsAgo)}";

        // A restart gives a new pid, so the rates start over.
        if (_previous is not { } previous || previous.Pid != s.Pid)
        {
            _previous = s;
            _cpu.Text = _wired.Text = "…";
            _cpuDetail.Text = $"pid {s.Pid}";
            return;
        }

        (double server, double core) = s.Cpu(previous);
        Dictionary<int, double> perThread = s.ThreadCpu(previous);
        double Cpu(JvmThread t) => perThread.GetValueOrDefault(t.Id);

        var wired = s.Threads.Where(t => EmulatorMonitor.Group(t.Name) == "Wired")
            .OrderBy(t => t.Name.StartsWith("WiredTickCoordi", StringComparison.Ordinal) ? 0 : t.Name.StartsWith("WiredTickShard", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(t => t.Id)
            .ToList();
        double wiredCpu = wired.Sum(Cpu);

        _cpu.Text = $"{core:0}%";
        _cpuDetail.Text = $"of one core · {server:0.0}% of the server ({s.Cores} cores) · pid {s.Pid}";
        _wired.Text = $"{wiredCpu:0.0}%";
        _wiredDetail.Text = core > 0 ? $"of one core · {wiredCpu * 100 / core:0}% of the emulator's CPU · {wired.Count} threads" : $"of one core · {wired.Count} threads";
        _chart.Add(core, wiredCpu);
        _chartScale.Text = $"Top of the graph: {_chart.Top:0}% of one core · last {_chart.Capacity * Interval.TotalSeconds / 60:0} minutes";

        _wiredThreads.Children.Clear();
        _wiredThreads.RowDefinitions.Clear();
        // The log numbers the heavy workers after the shards ("Slow wired task: shard=6" with 6 shards).
        int shardCount = wired.Count(t => t.Name.StartsWith("WiredTickShard-", StringComparison.Ordinal));
        string Label(JvmThread t) =>
            t.Name.StartsWith("WiredTickHeavy-", StringComparison.Ordinal) && int.TryParse(t.Name.AsSpan(15), out int heavy)
                ? $"Heavy rooms (shard {shardCount + heavy})"
                : EmulatorMonitor.WiredName(t.Name);
        foreach (JvmThread thread in wired)
            AddGridRow(_wiredThreads, Text(Label(thread), 12.5, weight: FontWeight.SemiBold), Bar(Math.Min(100, Cpu(thread))),
                Text($"{Cpu(thread):0.0}%", 12.5), Text(thread.State == 'R' ? "running" : "waiting", 12, "Pro.Muted"));
        if (wired.Count == 0)
            AddGridRow(_wiredThreads, Text("No wired threads: this emulator version has no WiredTickService.", 12.5, "Pro.Muted"));

        _groups.Children.Clear();
        _groups.RowDefinitions.Clear();
        foreach (var group in s.Threads.GroupBy(t => EmulatorMonitor.Group(t.Name)).Select(g => (Name: g.Key, Count: g.Count(), Cpu: g.Sum(Cpu)))
                     .OrderByDescending(g => g.Cpu).ThenBy(g => g.Name))
        {
            double share = core > 0 ? group.Cpu * 100 / core : 0;
            // A share of the emulator's CPU rather than a warning level, so a single colour.
            AddGridRow(_groups, Text(group.Name, 12.5, weight: FontWeight.SemiBold), Bar(Math.Min(100, share), EmuColor),
                Text($"{group.Cpu:0.0}%", 12.5), Text($"{group.Count} threads", 12, "Pro.Muted"));
        }
        _previous = s;
    }

    private async Task ReadLogAsync(ServerConnection? connection = null)
    {
        connection ??= _connection();
        if (connection is null) return;
        WiredLogSummary summary;
        try
        {
            summary = await Task.Run(() => EmulatorMonitor.WiredLogAsync(connection));
        }
        catch (Exception ex)
        {
            _logSummary.Text = $"Could not read the log: {ex.Message}";
            return;
        }

        _logSummary.Text = summary.SlowTasks == 0 && summary.DroppedWarnings == 0
            ? "No slow wired tasks since the emulator started: every room's wired finished in time."
            : $"{summary.SlowTasks} slow wired tasks in {summary.Rooms.Count} rooms" +
              (summary.DroppedWarnings > 0 ? $", and {summary.DroppedWarnings} times a room had so many wired tasks waiting that new ones were dropped." : ".");
        _rooms.Children.Clear();
        _rooms.RowDefinitions.Clear();
        if (summary.Rooms.Count > 0)
        {
            string[] headings = ["Room", "Name", "Slow tasks", "Slowest", "Total", "Shard / last seen"];
            AddGridRow(_rooms, headings.Select(h => (Control)Text(h, 11.5, "Pro.Muted", FontWeight.SemiBold)).ToArray());
            foreach (SlowWiredRoom room in summary.Rooms.Take(15))
                AddGridRow(_rooms, Mono(room.RoomId.ToString()), Text(room.Name ?? "?", 12.5),
                    Text(room.Count.ToString(), 12.5), Text($"{room.SlowestMs} ms", 12.5, room.SlowestMs >= 500 ? "Pro.ErrorText" : "Pro.Text"),
                    Text($"{room.TotalMs} ms", 12.5), Text($"{room.Shards} · {room.LastSeen}", 12, "Pro.Muted"));
        }
        _recent.Children.Clear();
        if (summary.Recent.Count > 0)
        {
            _recent.Children.Add(Text("Latest warnings", 11.5, "Pro.Muted", FontWeight.SemiBold));
            foreach (string line in summary.Recent)
            {
                SelectableTextBlock text = Mono(line);
                text.TextWrapping = TextWrapping.Wrap;
                _recent.Children.Add(text);
            }
        }
    }
}
