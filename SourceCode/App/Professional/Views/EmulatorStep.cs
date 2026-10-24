using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Builds the Polaris emulator and runs it as the "emulator" account with its own database account.</summary>
public sealed class EmulatorStep : InstallStep
{
    private static readonly int[] MemoryChoicesMb = [1024, 2048, 3072, 4096, 6144, 8192, 12288, 16384];

    private const string DefaultTimezone = "Europe/Amsterdam";

    // Typing the first letters (e.g. "Europe/Am") jumps to the zone.
    private readonly ComboBox _timezone = new() { MinWidth = 340, MaxDropDownHeight = 340, IsTextSearchEnabled = true };
    private readonly ComboBox _memory = new()
    {
        MinWidth = 160,
        ItemsSource = MemoryChoicesMb.Select(mb => $"{mb / 1024} GB").ToList(),
        SelectedIndex = 3
    };
    private readonly TextBox _whitelist = new() { MinWidth = 420, PlaceholderText = "localhost,127.0.0.1,hotel.example.com,*.hotel.example.com" };
    private readonly TextBlock _ramNote = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    public override string Title => "Emulator";
    public override string Caption => "Build & service";
    public override string InstallLabel => "Build and start the emulator";
    public override string RerunLabel => "Rebuild and restart";

    public EmulatorStep() : base(Icons.Cog,
        "Build and run the Polaris emulator",
        $"Builds {EmulatorInstaller.Directory} and runs it as a service under its own account.",
        [
            "Installs JDK 25 (OpenJDK, or Eclipse Temurin where the distribution has none) and builds with Emulator/build.sh.",
            "Creates the account 'emulator' (member of www-data); it owns /var/www/emulator and the log folder /var/log/emu.",
            "Creates a MariaDB account just for the emulator, with a generated password saved in the configuration folder.",
            "Writes config.ini, the start script and emulator.service, starts it at boot, and waits until the emulator has loaded."
        ],
        "The first start creates all database tables, which can take a minute.",
        [
            "Check the server",
            "Install JDK 25",
            "Create the emulator account and log folder",
            "Build the emulator (build.sh)",
            "Database account, config.ini and service",
            "Start the emulator"
        ])
    {
    }

    protected override Control BuildOptions()
    {
        static Control Labelled(string label, Control input, string? help = null)
        {
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(Text(label, 12, "Pro.Muted", Avalonia.Media.FontWeight.SemiBold));
            panel.Children.Add(input);
            if (help is not null) panel.Children.Add(Text(help, 11.5, "Pro.Subtle"));
            return panel;
        }

        _ramNote.Bind(TextBlock.ForegroundProperty, "Pro.Subtle");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(Labelled("Timezone (config.ini serverTimezone)", _timezone, "Type the first letters to jump, e.g. Europe/Am"));
        var memory = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        memory.Children.Add(_memory);
        memory.Children.Add(_ramNote);
        row.Children.Add(Labelled("Emulator memory (-Xmx)", memory));

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(row);
        panel.Children.Add(Labelled("Websocket whitelist (hosts the client may connect from)", _whitelist,
            "Comma separated; *.domain.com allows all subdomains. Add your hotel's domain here."));
        return panel;
    }

    public override string? Validate()
    {
        if (_timezone.SelectedItem is not TimeZoneChoice) return "Choose a timezone for the emulator.";
        string list = _whitelist.Text?.Trim() ?? "";
        if (list.Length == 0) return "Enter at least one host for the websocket whitelist (e.g. localhost).";
        if (list.Split(',').Any(h => h.Trim().Contains(' ') || h.Trim().Contains("://")))
            return "The whitelist holds host names only (no spaces or http://), separated by commas.";
        return null;
    }

    private int SelectedMemoryMb => MemoryChoicesMb[Math.Max(0, _memory.SelectedIndex)];

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        EmulatorState state = await Task.Run(() => EmulatorInstaller.InspectAsync(connection));
        FillOptions(state, connection.Login.Host);

        if (!state.RepositoryPresent)
        {
            Detected.Show(ResultKind.Warning, $"{EmulatorInstaller.Repository} is not there",
                "Run the Polaris clone step first: the emulator is built from that folder.");
            return false;
        }
        if (state.IsComplete)
        {
            Detected.Show(ResultKind.Success, "The emulator is built and running",
                $"{Path.GetFileName(state.Jar)} runs as 'emulator'{(state.ServiceInstalled ? " (emulator.service)" : "")}. " +
                "You can skip this step, or rebuild and restart it (e.g. after updating Polaris).");
            return true;
        }
        Detected.Show(ResultKind.Info, state.Jar is null ? "The emulator is not built yet" : "The emulator is built, but not running",
            "Choose the timezone, memory and whitelist below, then build and start it.");
        return false;
    }

    // Defaults: what is configured already, else Europe/Amsterdam and memory that fits the server's RAM.
    private void FillOptions(EmulatorState state, string host)
    {
        string wanted = (_timezone.SelectedItem as TimeZoneChoice)?.Id ?? state.ConfiguredTimezone ?? DefaultTimezone;
        List<TimeZoneChoice> choices = TimeZoneChoice.List(state.Timezones);
        _timezone.ItemsSource = choices;
        _timezone.SelectedItem = choices.FirstOrDefault(c => c.Id == wanted)
                                 ?? choices.FirstOrDefault(c => c.Id == DefaultTimezone)
                                 ?? choices.FirstOrDefault();

        int memory = state.ConfiguredMemoryMb ?? (state.RamMb >= 8192 ? 4096 : Math.Max(1024, state.RamMb / 2 / 1024 * 1024));
        _memory.SelectedIndex = Math.Max(0, Array.FindLastIndex(MemoryChoicesMb, mb => mb <= memory));
        _ramNote.Text = state.RamMb > 0 ? $"server has {state.RamMb / 1024.0:0.#} GB RAM" : "";

        if (string.IsNullOrWhiteSpace(_whitelist.Text))
            _whitelist.Text = state.ConfiguredWhitelist is { Length: > 0 } configured && configured != "*"
                ? configured
                : string.Join(',', new[] { "localhost", "127.0.0.1", host }.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string host = connection.Login.Host;
        string timezone = ((TimeZoneChoice)_timezone.SelectedItem!).Id;
        string whitelist = string.Join(',', _whitelist.Text!.Split(',').Select(h => h.Trim()).Where(h => h.Length > 0));
        int memoryMb = SelectedMemoryMb;

        // The emulator's database password is saved before anything changes.
        Run.Log("> Checking the server ...");
        EmulatorState state = await Task.Run(() => EmulatorInstaller.InspectAsync(connection));
        if (!state.RepositoryPresent)
            throw new ServerConnectionException($"{EmulatorInstaller.Repository} has no Emulator/pom.xml. Run the Polaris clone step first.");
        DatabaseCredentials credentials = ClientConfiguration.LoadDatabaseCredentials(host)
            ?? throw new ServerConnectionException("There are no saved MariaDB passwords for this server. Run the MariaDB step first.");
        if (credentials.EmulatorPassword.Length == 0)
        {
            credentials = credentials with { EmulatorPassword = DatabaseCredentials.NewPassword() };
            ClientConfiguration.SaveDatabaseCredentials(credentials);
            Run.Log($"  Generated a password for the MariaDB account '{credentials.EmulatorUser}', saved in {ClientConfiguration.DatabaseCredentialsPath(host)}");
        }
        Run.Log($"  Timezone {timezone}, memory {memoryMb / 1024} GB, whitelist {whitelist}");
        Run.Done(state.Jar is null ? "first build" : "rebuild");

        string javaHome = await Task.Run(() => EmulatorInstaller.InstallJavaAsync(connection, Run.Log));
        Run.Done(javaHome);
        await Task.Run(() => EmulatorInstaller.CreateAccountAsync(connection, Run.Log));
        Run.Done("emulator (www-data), /var/log/emu");
        string jar = await Task.Run(() => EmulatorInstaller.BuildAsync(connection, javaHome, Run.Log));
        Run.Done(Path.GetFileName(jar));

        DatabaseCredentials account = credentials;
        await Task.Run(() => EmulatorInstaller.ConfigureAsync(connection, account, timezone, memoryMb, javaHome, Run.Log));
        Run.Done($"{account.EmulatorUser}@localhost, config.ini, emulator.service");

        EmulatorStartResult started = await Task.Run(() => EmulatorInstaller.StartAsync(connection, whitelist, Run.Log));
        Run.Done("port 3000 listening");

        string boot = started.Systemd ? "It starts automatically at boot (emulator.service)." : "This machine has no systemd: it was started once, not at boot.";
        return await SucceededAsync("The emulator is running",
            $"{Path.GetFileName(started.Jar)} on port 3000. {boot} Log: {EmulatorInstaller.LogDirectory}/emulator.log" +
            (started.Errors > 0 ? $" ({started.Errors} ERROR lines, mostly hotel content messages: worth a look)." : "."));
    }
}

/// <summary>An IANA timezone in the dropdown, shown with its current UTC offset.</summary>
public sealed record TimeZoneChoice(string Id, string Label)
{
    public override string ToString() => Label;

    // The server's zones (what Java and MariaDB there accept); this computer's IANA zones when the server lists none.
    public static List<TimeZoneChoice> List(IReadOnlyList<string> serverZones)
    {
        IEnumerable<string> ids = serverZones.Count > 0 ? serverZones : LocalIanaZones();
        return ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Create).ToList();
    }

    private static TimeZoneChoice Create(string id)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone)) return new TimeZoneChoice(id, id);
        TimeSpan offset = zone.GetUtcOffset(DateTime.UtcNow);
        return new TimeZoneChoice(id, $"{id}  (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm})");
    }

    // Windows lists its own zone names; .NET converts them to IANA ids.
    private static IEnumerable<string> LocalIanaZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out string? iana) ? iana : null)
            .OfType<string>();
}
