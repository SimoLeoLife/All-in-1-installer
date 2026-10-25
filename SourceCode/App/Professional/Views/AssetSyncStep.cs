using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Deploy Assets step that copies the All-in-1 downloader's files to /var/www/gamedata.</summary>
public sealed class AssetSyncStep : InstallStep
{
    private readonly CheckBox _overwriteConfig = new()
    {
        Content = "Also replace config JSON files that are already on the server (FurnitureData, ProductData, ExternalTexts ...)"
    };
    private readonly Grid _table = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 16, RowSpacing = 6 };
    private readonly Dictionary<AssetMapping, CheckBox> _selected = [];
    private readonly TextBlock _source = Text("", 12, "Pro.Subtle");

    /// <summary>What was sent, for the summary page.</summary>
    public string? Summary { get; private set; }

    public override string Title => "Sync";
    public override string Caption => "Downloader";
    public override string InstallLabel => "Upload";
    public override string RerunLabel => "Sync again";

    public AssetSyncStep() : base(Icons.Download,
        "Sync from the All-in-1 downloader",
        "Copies the Habbo assets the All-in-1 downloader saved in its Habbo_Default folder to /var/www/gamedata on the server, where Octane loads them.",
        [
            "Compares every folder with the server first: only files that are not there yet, or that differ (size, or newer here), are sent.",
            "Sends them per folder as one stream over the SSH connection (no FTP, no temporary files); the files keep their dates.",
            "Never deletes anything on the server: your own custom furniture, badges and the emulator's files stay.",
            "Config JSON (FurnitureData, ProductData, ExternalTexts ...) is only added when missing, unless you tick that it may be replaced: hotels and the emulator edit those files.",
            $"Then gives the emulator and NGINX access again ({OctaneInstaller.PermissionsScript})."
        ],
        "Set the downloader's executable in Settings first. New furniture also needs rows in items_base and catalog_items: the All-in-1 converter makes the SQL (Hotel Tools → Generate SQL).",
        ["Compare the downloader with the server", "Check the free space", "Upload new and changed files", "Permissions (www-data)"])
    {
    }

    protected override Control BuildOptions()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _source.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_source);
        panel.Children.Add(_table);
        panel.Children.Add(_overwriteConfig);
        return panel;
    }

    private static string? Folder() => AssetSync.DownloaderFolder(InstallerSettings.Load().DownloaderPath);

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        string? folder = Folder();
        if (folder is null)
        {
            _source.Text = "";
            _table.Children.Clear();
            Detected.Show(ResultKind.Warning, "The All-in-1 downloader is not set",
                "Open Settings → All-in-1 downloader and choose its executable (its folder holds Habbo_Default), then come back here.");
            return false;
        }
        bool overwrite = _overwriteConfig.IsChecked == true;
        List<AssetPlan> plans = await Task.Run(() => AssetSync.PlanAsync(connection, folder, overwrite));
        ShowPlans(folder, plans);
        int count = plans.Sum(p => p.UploadCount);
        if (count == 0)
        {
            Detected.Show(ResultKind.Success, "The server has everything the downloader has",
                $"{plans.Sum(p => p.LocalCount)} files in {AssetSync.DefaultFolder}, all on the server. Download more with the All-in-1 downloader and sync again.");
            return true;
        }
        Detected.Show(ResultKind.Info, $"{count} files to upload ({ServerStatusView.Bytes(plans.Sum(p => p.UploadBytes))})",
            "Untick the folders you do not want, then upload.");
        return false;
    }

    private void ShowPlans(string folder, List<AssetPlan> plans)
    {
        _source.Text = $"From {Path.Combine(folder, AssetSync.DefaultFolder)} to {OctaneInstaller.Gamedata} on the server:";
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        string[] headings = ["Folder", "Server", "Here", "To upload"];
        AddGridRow(_table, headings.Select(h => (Control)Text(h, 11.5, "Pro.Muted", FontWeight.SemiBold)).ToArray());
        foreach (AssetPlan plan in plans)
        {
            if (!_selected.TryGetValue(plan.Mapping, out CheckBox? box))
                _selected[plan.Mapping] = box = new CheckBox { IsChecked = true };
            box.Content = plan.Mapping.Name;
            box.IsEnabled = plan.UploadCount > 0;
            string upload = plan.UploadCount == 0 ? (plan.LocalCount == 0 ? "nothing downloaded" : "up to date")
                : $"{plan.New.Count} new" + (plan.Changed.Count > 0 ? $", {plan.Changed.Count} changed" : "") + $" · {ServerStatusView.Bytes(plan.UploadBytes)}";
            AddGridRow(_table, box,
                Text($"{plan.Mapping.Target}/", 12, "Pro.Muted"),
                Text($"{plan.LocalCount} files · {ServerStatusView.Bytes(plan.LocalBytes)}", 12, "Pro.Muted"),
                Text(upload, 12, plan.UploadCount > 0 ? "Pro.Text" : "Pro.Subtle", plan.UploadCount > 0 ? FontWeight.SemiBold : FontWeight.Normal));
        }
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string folder = Folder() ?? throw new ServerConnectionException("Set the All-in-1 downloader in Settings first.");
        Run.Log($"> Comparing {Path.Combine(folder, AssetSync.DefaultFolder)} with {OctaneInstaller.Gamedata} ...");
        bool overwrite = _overwriteConfig.IsChecked == true;
        List<AssetPlan> plans = await Task.Run(() => AssetSync.PlanAsync(connection, folder, overwrite));
        var chosen = plans.Where(p => p.UploadCount > 0 && _selected.GetValueOrDefault(p.Mapping)?.IsChecked != false).ToList();
        foreach (AssetPlan plan in plans.Where(p => p.LocalCount > 0))
            Run.Log($"  {plan.Mapping.Name,-24} {plan.LocalCount,6} here · {plan.New.Count} new, {plan.Changed.Count} changed" +
                    (plan.UploadCount > 0 && !chosen.Contains(plan) ? " (skipped: unticked)" : ""));
        long total = chosen.Sum(p => p.UploadBytes);
        int files = chosen.Sum(p => p.UploadCount);
        Run.Done($"{files} files, {ServerStatusView.Bytes(total)}");

        if (files == 0)
        {
            Run.Done("not needed");
            Run.Done("nothing to send");
            Run.Done("not needed");
            Summary = "Nothing to upload: the server already has every file.";
            return await SucceededAsync("The server is up to date", Summary);
        }

        long free = await Task.Run(() => AssetSync.FreeSpaceAsync(connection));
        Run.Log($"  {ServerStatusView.Bytes(free)} free on the server, {ServerStatusView.Bytes(total)} needed");
        if (free < total + (512L << 20))
            throw new ServerConnectionException($"Not enough disk space on the server: {ServerStatusView.Bytes(total)} to upload, {ServerStatusView.Bytes(free)} free (keeping 512 MB spare).");
        Run.Done($"{ServerStatusView.Bytes(free)} free");

        var started = DateTime.UtcNow;
        long done = 0;
        foreach (AssetPlan plan in chosen)
        {
            Run.Log($"> {plan.Mapping.Name}: {plan.UploadCount} files ({ServerStatusView.Bytes(plan.UploadBytes)}) to {OctaneInstaller.Gamedata}/{plan.Mapping.Target}");
            long before = done;
            int lastPercent = -1;
            var lastLog = DateTime.UtcNow;
            await Task.Run(() => AssetSync.UploadAsync(connection, plan, sent =>
            {
                int percent = (int)((before + sent) * 100 / Math.Max(1, total));
                if (percent == lastPercent || (DateTime.UtcNow - lastLog).TotalSeconds < 3 && percent < 100) return;
                lastPercent = percent;
                lastLog = DateTime.UtcNow;
                double seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                Run.Log($"  {percent}% · {ServerStatusView.Bytes(before + sent)} of {ServerStatusView.Bytes(total)} · {ServerStatusView.Rate((before + sent) / seconds)}");
            }));
            done += plan.UploadBytes;
        }
        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Run.Log($"  {files} files in {elapsed:0} seconds ({ServerStatusView.Rate(total / Math.Max(0.1, elapsed))})");
        Run.Done($"{files} files");

        await Task.Run(() => AssetSync.FixPermissionsAsync(connection, Run.Log));
        Run.Done("www-data");

        Summary = $"{files} files ({ServerStatusView.Bytes(total)}) uploaded: " +
                  string.Join(", ", chosen.Select(p => $"{p.Mapping.Name.ToLowerInvariant()} {p.UploadCount}")) + ".";
        return await SucceededAsync("The assets are on the server",
            Summary + " Players see them after reloading the hotel. New furniture also needs its items_base / catalog_items rows (All-in-1 converter → Generate SQL).");
    }
}
