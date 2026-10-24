using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// "Install New Server": asks host, account and password one at a time, logs in over SSH, switches to root
/// (sudo when the account is not root), then installs an SSH key for root.
/// </summary>
public sealed class NewServerWizard : IAsyncDisposable
{
    private sealed record Step(string Title, string Caption, string Icon, string Heading, string Help, TextBox? Input);

    private readonly TextBox _host = new() { PlaceholderText = "192.168.1.10 or hotel.example.com (add :2222 for another SSH port)" };
    private readonly TextBox _user = new() { PlaceholderText = "root or a user with sudo rights" };
    private readonly TextBox _password = new() { PlaceholderText = "Password of the account", PasswordChar = '•', RevealPassword = false };
    private readonly Step[] _steps;
    private readonly Control[] _questions;
    private int _index;

    // Steps in Auto columns, the lines between them in star columns, so the row shrinks to the card width.
    private readonly Grid _stepper = new();
    private bool _compactStepper;
    private readonly ContentControl _body = new();
    private readonly TextBlock _validation = Text("", 12.5, "Pro.ErrorText");
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(18, 9), Classes = { "secondary" } };
    private readonly Button _next = new() { Padding = new Thickness(22, 9), Classes = { "primary" } };
    private readonly Button _disconnect = new() { Content = "Disconnect", Padding = new Thickness(18, 9), Classes = { "secondary" } };
    private readonly Button _abort = new() { Content = "Abort", Padding = new Thickness(18, 9), Classes = { "secondary" } };
    private readonly Button _anotherKey = new() { Content = "Install another key", Padding = new Thickness(18, 9), Classes = { "secondary" } };
    private readonly Button _skip = new() { Content = "Skip", Padding = new Thickness(18, 9), Classes = { "secondary" } };
    // What the check found when the step opened: already installed means Skip is the main action.
    private bool _keyAlreadyInstalled;
    private readonly Button _continue = new() { Padding = new Thickness(22, 9), Classes = { "primary" } };
    private readonly Button _openSite = new() { Padding = new Thickness(22, 9), Classes = { "primary" } };
    private Control? _finishView;
    private string? _siteUrl;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

    // Connect step
    private readonly AvaloniaList<string> _logLines = [];
    private readonly LogView _log;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, Foreground = Solid(Brand) };
    private readonly CheckList _checks = new("Log in over SSH", "Check operating system", "Switch to root");
    private readonly ResultBanner _result = new();
    private readonly Control _connectBody;

    // SSH key step
    private readonly SshKeyStep _keyStep = new();
    private KeyStepOutcome? _keyOutcome;

    // Install steps after the SSH key, in order. A new step only needs adding here.
    private readonly InstallStep[] _installSteps =
    [
        new NginxStep(), new PhpStep(), new MariaDbStep(), new DevToolsStep(),
        new CloneStep("Polaris", "Emulator", "duckietm/Polaris-Emulator", "/var/www/emulator",
            "The Polaris game server (emulator): Java sources, the database and the hotel assets."),
        new CloneStep("Octane", "Client", "duckietm/Octane", "/var/www/Octane",
            "The Octane browser client: the hotel's user interface."),
        new CloneStep("Renderer", "Octane-Renderer", "duckietm/Octane-Renderer", "/var/www/Octane-Renderer",
            "The Octane Renderer: draws rooms, avatars and furniture for Octane."),
        new EmulatorStep(), new WebAccessStep(), new OctaneStep(), new HardenStep()
    ];
    private readonly StepOutcome?[] _installOutcomes;
    // Failed this session and skipped afterwards, not fixed; for the summary.
    private readonly bool[] _installFailed;
    private readonly bool[] _installAlreadyDone;

    public Control View { get; }
    public ServerConnection? Connection { get; private set; }
    public bool IsBusy { get; private set; }
    public string StatusMessage { get; private set; } = "Ready.";
    public event Action? StateChanged;
    /// <summary>The user left the installation (Abort after an aborted run); the window shows the dashboard.</summary>
    public event Action? Exited;

    private int ConnectIndex => _steps.Length;
    private int KeyIndex => _steps.Length + 1;
    private bool OnConnectStep => _index == ConnectIndex;
    private bool OnKeyStep => _index == KeyIndex;
    private int FirstInstallIndex => _steps.Length + 2;
    private bool OnInstallStep => _index >= FirstInstallIndex && _index < FirstInstallIndex + _installSteps.Length;
    // Index of the summary page.
    private int FinishIndex => FirstInstallIndex + _installSteps.Length;
    private bool OnFinishStep => _index == FinishIndex;
    // Only meaningful while OnInstallStep.
    private int InstallStepIndex => _index - FirstInstallIndex;
    private bool OnQuestion => _index < _steps.Length;

    public NewServerWizard()
    {
        _steps =
        [
            new("Server", "IP / Hostname", Icons.Server, "Server IP / Hostname",
                "The address of the Ubuntu Server or Debian machine. SSH must be reachable (port 22 unless you add another one).", _host),
            new("Account", "User account", Icons.Account, "User account",
                "The account to log in with. When it is not root, the installer switches to root with sudo.", _user),
            new("Password", "Login password", Icons.Key, "Password",
                "Used for the SSH login and, for a non-root account, for sudo. It is only kept in memory while the installer runs.", _password)
        ];
        // Built once: a TextBox can only have one parent, so the panels are reused when the user steps back.
        _questions = _steps.Select(BuildQuestion).ToArray();
        _log = new LogView(_logLines) { Height = 220 };
        _connectBody = BuildConnectBody();

        foreach (TextBox box in new[] { _host, _user, _password })
        {
            box.FontSize = 14;
            box.Padding = new Thickness(12, 9);
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await NextAsync();
            };
            box.TextChanged += (_, _) => _validation.Text = "";
        }
        _back.Click += (_, _) => Back();
        _next.Click += async (_, _) => await NextAsync();
        _disconnect.Click += async (_, _) =>
        {
            if (IsBusy) return;
            await DisconnectAsync();
            _index = 0;
            Render();
        };
        _keyStep.Status += SetStatus;
        _installOutcomes = new StepOutcome?[_installSteps.Length];
        _installAlreadyDone = new bool[_installSteps.Length];
        _installFailed = new bool[_installSteps.Length];
        foreach (InstallStep step in _installSteps) step.Status += SetStatus;
        _continue.Click += async (_, _) =>
        {
            if (IsBusy || !OnInstallStep) return;
            if (InstallStepIndex + 1 < _installSteps.Length) await EnterInstallStepAsync(InstallStepIndex + 1);
            else await EnterFinishAsync();
        };
        _skip.Click += async (_, _) =>
        {
            if (IsBusy || Connection is null) return;
            if (OnKeyStep)
            {
                SetStatus("SSH key step skipped.");
                await EnterInstallStepAsync(0);
            }
            else if (OnInstallStep)
            {
                int i = InstallStepIndex;
                _installOutcomes[i] = StepOutcome.Skipped;
                if (i + 1 < _installSteps.Length)
                {
                    SetStatus($"{_installSteps[i].Title} step skipped.");
                    await EnterInstallStepAsync(i + 1);
                }
                else
                {
                    SetStatus($"{_installSteps[i].Title} step skipped.");
                    await EnterFinishAsync();
                }
            }
        };
        _openSite.Click += async (_, _) =>
        {
            if (_siteUrl is not null && TopLevel.GetTopLevel(View) is { } top)
                await top.Launcher.LaunchUriAsync(new Uri(_siteUrl));
        };
        _anotherKey.Click += async (_, _) =>
        {
            if (IsBusy || Connection is null) return;
            await EnterKeyStepAsync();
        };
        _abort.Click += async (_, _) =>
        {
            if (IsBusy) return;
            await DisconnectAsync();
            // Start clean next time; the password is not kept.
            _password.Text = "";
            _keyOutcome = null;
            Array.Clear(_installOutcomes);
            _index = 0;
            Render();
            SetStatus("Installation aborted.");
            Exited?.Invoke();
        };

        var layout = new StackPanel { Spacing = 22 };
        layout.Children.Add(_stepper);
        layout.Children.Add(new Border { Height = 1 }.Bind(Border.BackgroundProperty, "Pro.CardBorder"));
        layout.Children.Add(_body);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        _validation.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_validation);
        Grid.SetColumn(_buttons, 1);
        footer.Children.Add(_buttons);
        layout.Children.Add(footer);
        View = Card(layout, new Thickness(28, 24));
        // When the captions of all steps do not fit (about 115 px each), only the current step keeps its caption.
        View.SizeChanged += (_, e) =>
        {
            int count = _steps.Length + 2 + _installSteps.Length + 1;
            bool compact = e.NewSize.Width < count * 115;
            if (compact == _compactStepper) return;
            _compactStepper = compact;
            RenderStepper();
        };

        Render();
    }

    public void FocusCurrentStep() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (OnQuestion) _steps[_index].Input?.Focus();
            else if (OnKeyStep && !_keyStep.ShowingRun) _keyStep.FocusInput();
        }, DispatcherPriority.Background);

    // ---------------------------------------------------------------- navigation

    private void Back()
    {
        if (IsBusy || _index == 0 || _index >= KeyIndex) return;
        _index--;
        Render();
    }

    private async Task NextAsync()
    {
        if (IsBusy) return;
        if (OnConnectStep)
        {
            if (Connection is null)
            {
                // "Try again" after a failure starts over from the first question.
                _index = 0;
                Render();
                return;
            }
            await EnterKeyStepAsync();
            return;
        }
        if (OnKeyStep)
        {
            await KeyStepNextAsync();
            return;
        }
        if (OnInstallStep)
        {
            await InstallStepNextAsync();
            return;
        }

        string? error = Validate(_index);
        if (error is not null)
        {
            _validation.Text = error;
            _steps[_index].Input?.Focus();
            return;
        }

        _index++;
        Render();
        if (OnConnectStep) await ConnectAsync();
    }

    private string? Validate(int index)
    {
        switch (index)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(_host.Text)) return "Enter the server IP address or hostname.";
                (string host, int port) = ServerLogin.ParseHost(_host.Text);
                if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return "That is not a valid IP address or hostname.";
                if (port is < 1 or > 65535) return "The SSH port must be between 1 and 65535.";
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return "That is not a valid IP address or hostname.";
                return null;
            case 1:
                string user = _user.Text?.Trim() ?? "";
                if (user.Length == 0) return "Enter the user account to log in with.";
                if (user.Any(c => char.IsWhiteSpace(c) || c == ':' || c == '@')) return "A user name cannot contain spaces, ':' or '@'.";
                return null;
            case 2:
                return string.IsNullOrEmpty(_password.Text) ? "Enter the password." : null;
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- rendering

    private void Render()
    {
        _validation.Text = "";
        RenderStepper();
        _buttons.Children.Clear();

        if (OnConnectStep)
        {
            _body.Content = _connectBody;
            UpdateConnectButtons();
            return;
        }
        if (OnKeyStep)
        {
            _body.Content = _keyStep.View;
            UpdateKeyButtons();
            FocusCurrentStep();
            return;
        }
        if (OnInstallStep)
        {
            _body.Content = _installSteps[InstallStepIndex].View;
            UpdateInstallButtons();
            return;
        }
        if (OnFinishStep)
        {
            _body.Content = _finishView;
            if (!IsBusy)
            {
                _buttons.Children.Add(_disconnect);
                if (_siteUrl is not null)
                {
                    _openSite.Content = $"Open {_siteUrl}";
                    _buttons.Children.Add(_openSite);
                }
            }
            return;
        }

        _body.Content = _questions[_index];
        if (_index > 0) _buttons.Children.Add(_back);
        _next.Content = _index == _steps.Length - 1 ? "Connect" : "Next";
        _next.IsEnabled = true;
        _buttons.Children.Add(_next);
        FocusCurrentStep();
    }

    private void RenderStepper()
    {
        _stepper.Children.Clear();
        _stepper.ColumnDefinitions.Clear();
        var titles = _steps.Select(s => (s.Title, s.Caption))
            .Append((Title: "Connect", Caption: "Login & root"))
            .Append((Title: "SSH key", Caption: "authorized_keys"))
            .Concat(_installSteps.Select(s => (s.Title, s.Caption)))
            .Append((Title: "Completed", Caption: "Upload assets"))
            .ToArray();
        for (int i = 0; i < titles.Length; i++)
        {
            bool done = i < _index;
            bool current = i == _index;
            var number = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                VerticalAlignment = VerticalAlignment.Center,
                Child = done
                    ? Icon(Icons.Check, 15).Bind(PathIcon.ForegroundProperty, "Pro.SuccessText")
                    : new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = current ? Brushes.White : null
                    }
            };
            if (current) number.Background = Solid(Brand);
            else number.Bind(Border.BackgroundProperty, done ? "Pro.SuccessBack" : "Pro.Hover");
            if (!current && !done && number.Child is TextBlock idle) idle.Bind(TextBlock.ForegroundProperty, "Pro.Muted");

            var label = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(Text(titles[i].Title, 13, current ? "Pro.Text" : "Pro.Muted", current ? FontWeight.SemiBold : FontWeight.Normal));
            if (current || !_compactStepper) label.Children.Add(Text(titles[i].Caption, 11.5, "Pro.Subtle"));

            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            item.Children.Add(number);
            // Narrow card: only the current step shows its name; the others show a check mark or number, name on hover.
            if (_compactStepper && !current) ToolTip.SetTip(item, titles[i].Title);
            else item.Children.Add(label);
            _stepper.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(item, _stepper.ColumnDefinitions.Count - 1);
            _stepper.Children.Add(item);

            if (i < titles.Length - 1)
            {
                _stepper.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 16, MaxWidth = 60 });
                var line = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) }
                    .Bind(Border.BackgroundProperty, done ? "Pro.SuccessText" : "Pro.CardBorder");
                Grid.SetColumn(line, _stepper.ColumnDefinitions.Count - 1);
                _stepper.Children.Add(line);
            }
        }
    }

    private static Control BuildQuestion(Step step)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18 };
        var icon = Icon(step.Icon, 22);
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
        var panel = new StackPanel { Spacing = 8, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Text(step.Heading, 19, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(step.Help, 13, "Pro.Muted"));
        if (step.Input is not null)
        {
            step.Input.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(step.Input);
        }
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    private Control BuildConnectBody()
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(_progress);
        panel.Children.Add(_checks);
        panel.Children.Add(_result);
        panel.Children.Add(_log);
        return panel;
    }

    private void UpdateConnectButtons()
    {
        _buttons.Children.Clear();
        _progress.IsVisible = IsBusy;
        if (IsBusy) return;
        if (Connection is null)
        {
            _buttons.Children.Add(_back);
            _next.Content = "Try again";
        }
        else
        {
            _buttons.Children.Add(_disconnect);
            _next.Content = "Next: SSH key";
        }
        _buttons.Children.Add(_next);
    }

    private void UpdateKeyButtons()
    {
        _buttons.Children.Clear();
        if (IsBusy) return;
        SetPrimary(_next, true);
        switch (_keyOutcome)
        {
            case null:
                _buttons.Children.Add(_disconnect);
                AddInstallOrSkip(_keyAlreadyInstalled ? "Install another key" : "Install key", _keyAlreadyInstalled);
                return;
            case KeyStepOutcome.Installed:
                _buttons.Children.Add(_disconnect);
                _buttons.Children.Add(_anotherKey);
                _next.Content = $"Next: {_installSteps[0].Title}";
                break;
            case KeyStepOutcome.Failed:
                _buttons.Children.Add(_disconnect);
                _next.Content = "Try again";
                break;
            case KeyStepOutcome.Aborted:
                _buttons.Children.Add(_abort);
                _next.Content = "Start over";
                break;
        }
        _buttons.Children.Add(_next);
    }

    private async Task KeyStepNextAsync()
    {
        if (_keyOutcome is KeyStepOutcome.Aborted || Connection is null)
        {
            _index = 0;
            Render();
            return;
        }
        if (_keyOutcome is KeyStepOutcome.Installed)
        {
            await EnterInstallStepAsync(0);
            return;
        }
        if (_keyOutcome is not null)
        {
            // "Try again": back to the choices, with a fresh look at the server.
            await EnterKeyStepAsync();
            return;
        }

        string? error = _keyStep.Validate();
        if (error is not null)
        {
            _validation.Text = error;
            return;
        }
        if (TopLevel.GetTopLevel(View) is not Window owner) return;

        _validation.Text = "";
        IsBusy = true;
        UpdateKeyButtons();
        StateChanged?.Invoke();
        try
        {
            _keyOutcome = await _keyStep.RunAsync(Connection, owner);
            if (_keyOutcome == KeyStepOutcome.Aborted)
            {
                await DisconnectAsync();
                SetStatus("Installation aborted. Nothing was changed on the server.");
            }
        }
        finally
        {
            IsBusy = false;
            RenderStepper();
            UpdateKeyButtons();
            StateChanged?.Invoke();
        }
    }

    // Install and Skip side by side; the one that fits the check result is highlighted, on the right.
    private void AddInstallOrSkip(string installText, bool alreadyInstalled)
    {
        _next.Content = installText;
        _skip.Content = alreadyInstalled ? "Skip (already installed)" : "Skip";
        SetPrimary(_next, !alreadyInstalled);
        SetPrimary(_skip, alreadyInstalled);
        if (alreadyInstalled)
        {
            _buttons.Children.Add(_next);
            _buttons.Children.Add(_skip);
        }
        else
        {
            _buttons.Children.Add(_skip);
            _buttons.Children.Add(_next);
        }
    }

    private static void SetPrimary(Button button, bool primary)
    {
        button.Classes.Set("primary", primary);
        button.Classes.Set("secondary", !primary);
    }

    private async Task EnterKeyStepAsync()
    {
        if (Connection is null) return;
        _index = KeyIndex;
        _keyOutcome = null;
        _keyStep.ShowOptions(Connection.Login.Host);
        await CheckAsync("Checking the installed SSH keys ...", () => _keyStep.DetectAsync(Connection), found => _keyAlreadyInstalled = found);
    }

    private async Task EnterInstallStepAsync(int i)
    {
        if (Connection is null) return;
        InstallStep step = _installSteps[i];
        _index = FirstInstallIndex + i;
        _installOutcomes[i] = null;
        step.ShowIntro();
        await CheckAsync($"Checking {step.Title} on the server ...", () => step.DetectAsync(Connection), found => _installAlreadyDone[i] = found);
    }

    // Title plus caption when another step shares the title (Octane clone / build).
    private string StepName(int i) =>
        _installSteps.Count(s => s.Title == _installSteps[i].Title) > 1 ? $"{_installSteps[i].Title} ({_installSteps[i].Caption})" : _installSteps[i].Title;

    private async Task EnterFinishAsync()
    {
        if (Connection is null) return;
        ServerConnection connection = Connection;
        _index = FinishIndex;
        _finishView = null;
        IsBusy = true;
        Render();
        SetStatus("Collecting the summary ...");
        try
        {
            WebAccessState web = await Task.Run(() => WebAccessInstaller.InspectAsync(connection));
            _siteUrl = web.Configured?.Url;
            string host = connection.Login.Host;
            string? ppk = Directory.Exists(ClientConfiguration.KeysDirectory(host))
                ? Directory.GetFiles(ClientConfiguration.KeysDirectory(host), "*.ppk").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;

            var steps = new List<(string, StepSummary)>
            {
                ("SSH key", _keyOutcome == KeyStepOutcome.Installed ? StepSummary.Done
                    : _keyAlreadyInstalled ? StepSummary.AlreadyInstalled
                    : _keyOutcome == KeyStepOutcome.Failed ? StepSummary.Failed : StepSummary.Skipped)
            };
            for (int i = 0; i < _installSteps.Length; i++)
                steps.Add((StepName(i), _installOutcomes[i] switch
                {
                    StepOutcome.Done => StepSummary.Done,
                    _ when _installFailed[i] => StepSummary.Failed,
                    _ when _installAlreadyDone[i] => StepSummary.AlreadyInstalled,
                    StepOutcome.Skipped => StepSummary.Skipped,
                    _ => StepSummary.NotRun
                }));
            _finishView = FinishedView.Build(host, connection.Login.Port, web.Configured, ppk, steps);
            SetStatus("Installation completed: upload the assets.");
        }
        finally
        {
            IsBusy = false;
            Render();
        }
    }

    // Runs the check with the buttons hidden, then renders the step with the right buttons.
    private async Task CheckAsync(string status, Func<Task<bool>> check, Action<bool> store)
    {
        IsBusy = true;
        Render();
        SetStatus(status);
        bool found = false;
        try
        {
            found = await check();
        }
        finally
        {
            // Stored before Render, which picks the buttons from it.
            store(found);
            IsBusy = false;
            Render();
            SetStatus("Ready.");
        }
    }

    private void UpdateInstallButtons()
    {
        _buttons.Children.Clear();
        if (IsBusy) return;
        int i = InstallStepIndex;
        InstallStep step = _installSteps[i];
        bool hasNext = i + 1 < _installSteps.Length;
        _continue.Content = hasNext ? $"Next: {_installSteps[i + 1].Title}" : "Finish";
        SetPrimary(_next, true);
        _buttons.Children.Add(_installOutcomes[i] == StepOutcome.Aborted ? _abort : _disconnect);
        switch (_installOutcomes[i])
        {
            case null:
                bool done = _installAlreadyDone[i];
                AddInstallOrSkip(done ? step.RerunLabel : step.InstallLabel, done);
                break;
            case StepOutcome.Skipped:
                // Only the last step stays on screen when skipped; the user can still change their mind.
                _next.Content = $"{step.InstallLabel} anyway";
                SetPrimary(_next, false);
                _buttons.Children.Add(_next);
                break;
            case StepOutcome.Done:
                _buttons.Children.Add(_continue);
                break;
            case StepOutcome.Failed:
                _skip.Content = "Skip";
                SetPrimary(_skip, false);
                _buttons.Children.Add(_skip);
                _next.Content = "Try again";
                _buttons.Children.Add(_next);
                break;
            case StepOutcome.Aborted:
                _next.Content = "Start over";
                _buttons.Children.Add(_next);
                break;
        }
    }

    private async Task InstallStepNextAsync()
    {
        int i = InstallStepIndex;
        if (_installOutcomes[i] is StepOutcome.Aborted || Connection is null)
        {
            _index = 0;
            Render();
            return;
        }
        if (TopLevel.GetTopLevel(View) is not Window owner) return;
        if (_installSteps[i].Validate() is { } problem)
        {
            _validation.Text = problem;
            return;
        }
        _validation.Text = "";

        IsBusy = true;
        UpdateInstallButtons();
        StateChanged?.Invoke();
        try
        {
            _installOutcomes[i] = await _installSteps[i].RunAsync(Connection, owner);
            _installFailed[i] = _installOutcomes[i] == StepOutcome.Failed;
            if (_installOutcomes[i] == StepOutcome.Aborted)
            {
                await DisconnectAsync();
                SetStatus("Installation aborted. Nothing was changed on the server.");
            }
        }
        finally
        {
            IsBusy = false;
            RenderStepper();
            UpdateInstallButtons();
            StateChanged?.Invoke();
        }
    }

    // ---------------------------------------------------------------- connecting

    private async Task ConnectAsync()
    {
        (string host, int port) = ServerLogin.ParseHost(_host.Text ?? "");
        var login = new ServerLogin(host, port, _user.Text!.Trim(), _password.Text ?? "");

        IsBusy = true;
        _logLines.Clear();
        _log.ResetFollow();
        _result.Hide();
        _checks.Reset();
        int stage = 0;
        UpdateConnectButtons();
        SetStatus($"Connecting to {host}:{port} ...");

        void Log(string line) => Dispatcher.UIThread.Post(() =>
        {
            _logLines.Add(line);
            _log.FollowOutput();
        });
        void Stage(ConnectStage reached) => Dispatcher.UIThread.Post(() =>
        {
            if ((int)reached <= stage) return;
            stage = (int)reached;
            _checks.Update(stage, failed: false);
        });

        try
        {
            // SSH.NET does blocking work while connecting; keep it off the UI thread.
            Connection = await Task.Run(() => ServerConnection.ConnectAsync(login, Log, Stage));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _checks.SetDetail(0, $"{Connection.LoginUser}@{host}:{port}");
            _checks.SetDetail(1, Connection.Os?.PrettyName ?? "");
            _checks.SetDetail(2, Connection.LoginUser == "root" ? "logged in as root" : "through sudo");
            stage = _checks.Count;
            _checks.Update(stage, failed: false);
            _result.Show(ResultKind.Success, $"Connected to {host} as root",
                $"{Connection.Os?.PrettyName}. Next, install an SSH key for root.");
            SetStatus($"Connected to {host} as root.");
        }
        catch (Exception ex)
        {
            string message = ex is ServerConnectionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            // Log lines and stages are posted; let them land before the failed stage is painted.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _logLines.Add("! " + message);
            _log.FollowOutput();
            _checks.Update(stage, failed: true);
            _result.Show(ResultKind.Error, "Connection failed", message);
            SetStatus("Connection failed.");
        }
        finally
        {
            IsBusy = false;
            UpdateConnectButtons();
            StateChanged?.Invoke();
        }
    }

    private async Task DisconnectAsync()
    {
        if (Connection is null) return;
        string host = Connection.Login.Host;
        await Connection.DisposeAsync();
        Connection = null;
        SetStatus($"Disconnected from {host}.");
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null) await Connection.DisposeAsync();
        Connection = null;
    }
}
