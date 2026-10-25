using Avalonia;
using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Deploy Assets step that installs one of Octane's UITexts_&lt;language&gt;.jsonc.example files as gamedata/config/UITexts.jsonc.</summary>
public sealed class UiTextsStep : InstallStep
{
    private const string DefaultLanguage = "en";

    private readonly ComboBox _language = new() { MinWidth = 300, MaxDropDownHeight = 340 };
    private readonly TextBlock _note = Text("", 11.5, "Pro.Subtle");
    private List<string> _codes = [.. UiTextsInstaller.KnownLanguages];

    /// <summary>What was installed, for the summary page.</summary>
    public string? Summary { get; private set; }

    public override string Title => "UI texts";
    public override string Caption => "Language";
    public override string InstallLabel => "Install the UI texts";
    public override string RerunLabel => "Change the language";

    public UiTextsStep() : base(Icons.Globe,
        "The client's UI texts",
        $"Octane loads its own interface texts (buttons, windows, messages) from {UiTextsInstaller.Target}. " +
        "Octane comes with them in several languages; choose the one for your hotel.",
        [
            $"Takes UITexts_<language>.jsonc.example from {OctaneInstaller.ConfigurationDirectory} (the Octane clone), or downloads it from github.com/duckietm/Octane when the clone is not there.",
            "Installs it as UITexts.jsonc in /var/www/gamedata/config, readable by NGINX.",
            "A UITexts.jsonc of your own (one that is none of Octane's languages) is kept as UITexts.jsonc.bak-<time>, so your changes are not lost."
        ],
        "Players see the new texts after reloading the hotel. The game's own texts (furniture names, badges) are in ExternalTexts.json.",
        ["Find the language file", "Install UITexts.jsonc"])
    {
    }

    protected override Control BuildOptions()
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Text("Language", 12, "Pro.Muted", Avalonia.Media.FontWeight.SemiBold));
        panel.Children.Add(_language);
        _note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        panel.Children.Add(_note);
        ShowLanguages(null);
        return panel;
    }

    private void ShowLanguages(string? select)
    {
        string? keep = _language.SelectedIndex >= 0 && _language.SelectedIndex < _codes.Count ? _codes[_language.SelectedIndex] : null;
        _language.ItemsSource = _codes.Select(UiTextsInstaller.Name).ToList();
        // Detection runs before the user picks a language, so the installed one wins over the default.
        string wanted = select ?? keep ?? DefaultLanguage;
        int index = _codes.IndexOf(wanted);
        _language.SelectedIndex = index >= 0 ? index : Math.Max(0, _codes.IndexOf(DefaultLanguage));
    }

    public override string? Validate() => _language.SelectedIndex < 0 ? "Choose a language." : null;

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        UiTextsState state = await Task.Run(() => UiTextsInstaller.InspectAsync(connection));
        _codes = [.. state.Languages];
        ShowLanguages(state.Current is { } current && current != "custom" ? current : null);
        _note.Text = state.FromClone
            ? $"{_codes.Count} languages in the Octane clone on the server."
            : "No Octane clone with UITexts files on the server: the file is downloaded from GitHub.";

        switch (state.Current)
        {
            case null:
                Detected.Show(ResultKind.Info, "There is no UITexts.jsonc yet",
                    "Octane's renderer-config loads it from gamedata/config: choose a language and install it.");
                return false;
            case "custom":
                Detected.Show(ResultKind.Info, "UITexts.jsonc is your own version",
                    "It matches none of Octane's languages (edited, or from an older Octane). Installing a language keeps it as a backup.");
                return false;
            default:
                Detected.Show(ResultKind.Success, $"UITexts.jsonc is {UiTextsInstaller.Name(state.Current)}",
                    "It is the same as Octane's file. Choose another language to change it, or skip.");
                return true;
        }
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string code = _codes[_language.SelectedIndex];
        Run.Log($"> UITexts: {UiTextsInstaller.Name(code)}");
        Run.Done(code);
        string? backup = await Task.Run(() => UiTextsInstaller.InstallAsync(connection, code, Run.Log));
        Run.Done(UiTextsInstaller.Target);
        Summary = $"UITexts.jsonc is {UiTextsInstaller.Name(code)}." + (backup is null ? "" : $" The previous file is {backup}.");
        return await SucceededAsync("The UI texts are installed", Summary + " Players see them after reloading the hotel.");
    }
}
