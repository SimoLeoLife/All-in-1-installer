using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AllInOneInstaller.Remote;
using AllInOneInstaller.Terminal;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// Interactive SSH shell over the Tools login. The session survives switching pages and ends on exit or when
/// Tools disconnects.
/// </summary>
public sealed class TerminalView : Grid
{
    private readonly Func<ServerConnection?> _connection;
    private readonly TerminalControl _terminal = new();
    private readonly TextBlock _title = Text("", 13, weight: FontWeight.SemiBold);
    private readonly TextBlock _state = Text("", 12, "Pro.Muted");
    private readonly Button _newSession = new() { Content = "New session", Padding = new Thickness(14, 7), Classes = { "secondary" } };
    private readonly TextBlock _sshCommand = new() { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private TerminalSession? _session;
    private ServerConnection? _sessionConnection;
    private bool _titleQueued;
    private DispatcherTimer? _noticeTimer;

    public event Action? BackRequested;

    public TerminalView(Func<ServerConnection?> connection)
    {
        _connection = connection;
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        RowSpacing = 10;
        Margin = new Thickness(32, 0, 32, 18);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.Children.Add(_title);
        heading.Children.Add(_state);
        bar.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _newSession.Click += (_, _) => Start(restart: true);
        buttons.Children.Add(_newSession);
        buttons.Children.Add(ToolButton("Copy", async () => await _terminal.CopyAsync()));
        buttons.Children.Add(ToolButton("Paste", async () => await _terminal.PasteAsync()));
        buttons.Children.Add(ToolButton("A−", () => { _terminal.TerminalFontSize -= 1; return Task.CompletedTask; }));
        buttons.Children.Add(ToolButton("A+", () => { _terminal.TerminalFontSize += 1; return Task.CompletedTask; }));
        var back = new Button { Content = "Back to the tools", Padding = new Thickness(14, 7), Classes = { "secondary" } };
        back.Click += (_, _) => BackRequested?.Invoke();
        buttons.Children.Add(back);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        Children.Add(bar);

        var frame = new Border
        {
            Background = new SolidColorBrush(TerminalControl.Background),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = _terminal
        };
        Grid.SetRow(frame, 1);
        Children.Add(frame);

        var help = new StackPanel { Spacing = 4 };
        help.Children.Add(Text("Select with the mouse (double-click: a word) · Ctrl+Shift+C copies · Ctrl+Shift+V or the middle button pastes · " +
                               "Shift+PgUp / the wheel scrolls back · Ctrl + / − text size · right-click for more.", 11.5, "Pro.Subtle"));
        var own = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        own.Children.Add(Text("Your own SSH client:", 11.5, "Pro.Subtle"));
        _sshCommand.Bind(TextBlock.ForegroundProperty, "Pro.Muted");
        _sshCommand.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_sshCommand, 1);
        own.Children.Add(_sshCommand);
        var copyCommand = new Button { Content = "Copy", Padding = new Thickness(8, 2), FontSize = 11, Classes = { "secondary" } };
        copyCommand.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_sshCommand.Text ?? "");
            ShowNotice("ssh command copied.");
        };
        Grid.SetColumn(copyCommand, 2);
        own.Children.Add(copyCommand);
        help.Children.Add(own);
        Grid.SetRow(help, 2);
        Children.Add(help);

        _terminal.Notice += ShowNotice;
        AttachedToVisualTree += (_, _) => Start(restart: false);
    }

    private static Button ToolButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7), Classes = { "secondary" } };
        button.Click += async (_, _) => await action();
        return button;
    }

    // Reuses a running shell on the same connection unless restart is set.
    private void Start(bool restart)
    {
        ServerConnection? connection = _connection();
        if (connection is null || !connection.IsConnected)
        {
            _title.Text = "Not connected";
            _state.Text = "Log in on the Tools page first.";
            return;
        }
        ShowCommand(connection);
        if (!restart && _session is { IsClosed: false } && _sessionConnection == connection)
        {
            Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Input);
            return;
        }

        _session?.Dispose();
        _session = null;
        try
        {
            // Placeholder size until the first layout pass, when the terminal resizes the shell.
            int columns = _terminal.Bounds.Width > 50 ? _terminal.Columns : 120;
            int rows = _terminal.Bounds.Height > 50 ? _terminal.Rows : 32;
            var session = new TerminalSession(connection, columns, rows);
            session.Output += QueueTitle;
            session.Closed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session) return;
                _state.Text = "The session has ended (exit, or the connection was closed). Click New session to start another.";
                _newSession.Classes.Remove("secondary");
                _newSession.Classes.Add("primary");
            });
            _session = session;
            _sessionConnection = connection;
            _terminal.Session = session;
            _newSession.Classes.Remove("primary");
            if (!_newSession.Classes.Contains("secondary")) _newSession.Classes.Add("secondary");
            _title.Text = $"{connection.Login.User}@{connection.Login.Host}";
            _state.Text = $"Connected · {connection.Os?.PrettyName ?? "Linux"} · type exit to end the session";
            Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Input);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or IOException)
        {
            _title.Text = "No shell";
            _state.Text = "The server did not open a shell: " + ex.Message;
        }
    }

    private void ShowCommand(ServerConnection connection)
    {
        ServerLogin login = connection.Login;
        string key = login.PrivateKeys is { Count: > 0 } keys ? $"-i \"{keys[0]}\" " : "";
        string port = login.Port != 22 ? $"-p {login.Port} " : "";
        _sshCommand.Text = $"ssh {key}{port}{login.User}@{login.Host}";
    }

    // A window title set by the remote program (OSC 0/2) replaces user@host.
    private void QueueTitle()
    {
        if (_titleQueued) return;
        _titleQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _titleQueued = false;
            if (_session is null) return;
            string title;
            lock (_session.Emulator.Sync) title = _session.Emulator.Title;
            if (title.Length > 0) _title.Text = title;
        }, DispatcherPriority.Background);
    }

    private void ShowNotice(string text)
    {
        string previous = _state.Text ?? "";
        if (_noticeTimer is not null) { _noticeTimer.Stop(); previous = (string)_noticeTimer.Tag!; }
        _state.Text = text;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5), Tag = previous };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer!.Stop();
            _state.Text = (string)_noticeTimer.Tag!;
            _noticeTimer = null;
        };
        _noticeTimer.Start();
    }
}
