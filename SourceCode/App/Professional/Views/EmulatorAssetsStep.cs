using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Deploy Assets step that copies the client images and badge parts from Polaris' Assets/ folder into gamedata.</summary>
public sealed class EmulatorAssetsStep : InstallStep
{
    private readonly Grid _table = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 6 };

    /// <summary>What was copied, for the summary page.</summary>
    public string? Summary { get; private set; }

    public override string Title => "Images";
    public override string Caption => "From Polaris";
    public override string InstallLabel => "Copy the missing files";
    public override string RerunLabel => "Check again";

    public EmulatorAssetsStep() : base(Icons.Server,
        "The emulator's images and group badge parts",
        $"The client images and group badge parts that are not in the Habbo downloads: Polaris ships them in {EmulatorAssetsInstaller.ServerAssets}, and the installer has them in Assets/ next to its executable. " +
        "Octane needs them: when its preload images (loading_icon, clear_icon, big_arrow) are missing the client does not start, " +
        "and the emulator logs \"invalid SSO ticket\" for every login.",
        [
            $"Assets/images → {OctaneInstaller.Gamedata}/images (the preload images, wallet, navigator and catalogue extras).",
            $"Assets/images/reception → {OctaneInstaller.Gamedata}/c_images/reception (the hotel view in Octane's ui-config).",
            $"Assets/badgeparts → {OctaneInstaller.Gamedata}/c_images/groupbadge/badgeparts (group badges, for Octane and the emulator's badge imager).",
            $"Every other folder in the installer's Assets goes to the same path in gamedata: Assets/c_images (talent, Habbo Way, SnowStorm ...) → {OctaneInstaller.Gamedata}/c_images.",
            "Copies from the emulator's clone on the server first; what it does not have is uploaded from the installer's Assets folder (put your own extra images there too).",
            "Only adds missing files: images you changed or added yourself stay as they are."
        ],
        "Install New Server does this in the Octane step and Tools → Update emulator after every pull; run it here for a server installed before that.",
        ["Compare with gamedata", "Copy the missing files"])
    {
    }

    protected override Control BuildOptions()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_table);
        return panel;
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        List<EmulatorAssetState> states = await Task.Run(() => EmulatorAssetsInstaller.InspectAsync(connection));
        ShowStates(states);
        if (states.All(s => s.Total == 0))
        {
            Detected.Show(ResultKind.Warning, "No assets to copy",
                $"Neither the emulator's clone ({EmulatorAssetsInstaller.ServerAssets}) nor the installer ({EmulatorAssetsInstaller.LocalAssets}) has an Assets folder.");
            return false;
        }
        int missing = states.Sum(s => s.Missing);
        if (missing == 0)
        {
            Detected.Show(ResultKind.Success, "gamedata has all of the emulator's assets",
                $"{states.Sum(s => s.Total)} files, all in place.");
            return true;
        }
        string[] preloadImages = ["loading_icon.png", "clear_icon.png", "big_arrow.png"];
        bool preload = states[0].FromServer.Concat(states[0].FromInstaller).Any(preloadImages.Contains);
        Detected.Show(preload ? ResultKind.Warning : ResultKind.Info, $"{missing} files are missing in gamedata",
            preload
                ? "Among them are Octane's preload images: without them the client does not start (\"invalid SSO ticket\" in the emulator's log). Copy them now."
                : "Copy them now; nothing in gamedata is replaced.");
        return false;
    }

    private void ShowStates(List<EmulatorAssetState> states)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        AddGridRow(_table, Text("Folder", 11.5, "Pro.Muted", FontWeight.SemiBold), Text("Server", 11.5, "Pro.Muted", FontWeight.SemiBold),
            Text("Missing", 11.5, "Pro.Muted", FontWeight.SemiBold));
        foreach (EmulatorAssetState state in states)
        {
            var name = new StackPanel { Spacing = 1 };
            name.Children.Add(Text(state.Folder.Name, 13, weight: FontWeight.SemiBold));
            var purpose = Text(state.Folder.Purpose, 11.5, "Pro.Subtle");
            purpose.TextWrapping = TextWrapping.Wrap;
            purpose.MaxWidth = 380;
            name.Children.Add(purpose);
            string missing = state.Total == 0 ? "no source" : state.Missing == 0 ? $"none ({state.Total} files)"
                : $"{state.Missing} of {state.Total}" + (state.FromInstaller.Count > 0 ? $" ({state.FromInstaller.Count} from the installer)" : "");
            AddGridRow(_table, name, Text($"gamedata/{state.Folder.Target}/", 12, "Pro.Muted"),
                Text(missing, 12, state.Missing > 0 ? "Pro.Text" : "Pro.Subtle", state.Missing > 0 ? FontWeight.SemiBold : FontWeight.Normal));
        }
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        Run.Log($"> Comparing {EmulatorAssetsInstaller.ServerAssets} and the installer's Assets with {OctaneInstaller.Gamedata} ...");
        List<EmulatorAssetState> states = await Task.Run(() => EmulatorAssetsInstaller.InspectAsync(connection));
        foreach (EmulatorAssetState state in states)
            Run.Log($"  {state.Folder.Name}: {state.Missing} of {state.Total} missing ({state.FromServer.Count} in the emulator's clone, {state.FromInstaller.Count} only in the installer)");
        Run.Done($"{states.Sum(s => s.Missing)} missing");

        int added = await Task.Run(() => EmulatorAssetsInstaller.InstallAsync(connection, Run.Log));
        Run.Done($"{added} added");
        ShowStates(await Task.Run(() => EmulatorAssetsInstaller.InspectAsync(connection)));
        Summary = added == 0 ? "gamedata already had the emulator's images and badge parts." : $"{added} of the emulator's images and badge parts copied into gamedata.";
        return await SucceededAsync(added == 0 ? "Nothing was missing" : "The emulator's assets are in gamedata",
            Summary + " Reload the hotel (Ctrl+F5) to start the client with them.");
    }
}
