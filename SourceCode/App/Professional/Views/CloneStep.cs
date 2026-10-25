using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AllInOneInstaller.Configuration;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Clones a hotel repository on the server, or updates an existing clone.</summary>
public sealed class CloneStep : InstallStep
{
    private enum ExistingFolderChoice { MoveAside, Abort }

    private readonly ComboBox _branch = new() { MinWidth = 380, ItemsSource = InstallerSettings.BranchChoices, SelectedIndex = 0 };
    private readonly TextBlock _branchNote = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly string _url;
    private readonly string _repository;
    private readonly string _target;
    private readonly string _name;
    // The branch of the clone on the server, as the last check found it; null when there is no clone.
    private string? _clonedBranch;
    // Set while the saved branch is put in the list, so that does not count as a change to save again.
    private bool _settingBranch;

    private string SelectedBranch =>
        (_branch.SelectedItem as InstallerSettings.BranchChoice ?? InstallerSettings.BranchChoices[0]).Branch;

    public override string Title { get; }
    public override string Caption { get; }
    public override string InstallLabel => $"Clone {_name}";
    public override string RerunLabel => "Update (git pull)";

    public CloneStep(string title, string caption, string repository, string target, string description)
        : base(Icons.Download,
            $"Clone {repository}",
            description,
            [
                $"Clones https://github.com/{repository} to {target} (the full history, so it can be updated with git pull).",
                "You choose the branch: main for the stable version, dev for the newest changes.",
                "Installs git first when the server does not have it.",
                "Already cloned there? Then it is updated with git pull --ff-only (switching branch first when you picked another one); changed files are never overwritten.",
                "Something else in the folder? You choose: move it aside to a .bak folder, or abort. Nothing is deleted."
            ],
            null,
            ["Check the folder", "Clone or update", "Check the clone"])
    {
        Title = title;
        Caption = caption;
        _repository = repository;
        _url = $"https://github.com/{repository}.git";
        _target = target;
        _name = repository.Split('/').Last();

        ShowSavedBranch();
        _branch.SelectionChanged += (_, _) => BranchChosen();
    }

    /// <summary>Puts the saved branch in the list; the Settings page may have changed it since this step was made.</summary>
    private void ShowSavedBranch()
    {
        _settingBranch = true;
        _branch.SelectedItem = InstallerSettings.ChoiceFor(InstallerSettings.Load().BranchFor(_repository));
        _settingBranch = false;
    }

    /// <summary>The chosen branch stays chosen for this repository, also on the Settings page and next time.</summary>
    private void BranchChosen()
    {
        if (_settingBranch) return;
        InstallerSettings settings = InstallerSettings.Load();
        settings.SetBranch(_repository, SelectedBranch);
        settings.Save();
        ShowBranchNote();
    }

    protected override Control BuildOptions()
    {
        _branchNote.Bind(TextBlock.ForegroundProperty, "Pro.Subtle");
        _branchNote.IsVisible = false;
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Text("Branch", 12, "Pro.Muted", FontWeight.SemiBold));
        panel.Children.Add(_branch);
        panel.Children.Add(_branchNote);
        return panel;
    }

    private void ShowBranchNote()
    {
        _branchNote.IsVisible = _clonedBranch is not null;
        if (_clonedBranch is null) return;
        _branchNote.Text = SameBranch(_clonedBranch)
            ? $"{_target} is on {_clonedBranch}: the step updates that branch."
            : $"{_target} is on {_clonedBranch}: the step switches to {SelectedBranch} and updates it.";
    }

    private bool SameBranch(string? branch) => string.Equals(branch, SelectedBranch, StringComparison.OrdinalIgnoreCase);

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        ShowSavedBranch();
        CloneState state = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, _url, _target));
        _clonedBranch = state.Kind == CloneTargetKind.SameRepository ? state.Branch : null;
        ShowBranchNote();
        switch (state.Kind)
        {
            case CloneTargetKind.SameRepository:
                bool otherBranch = !SameBranch(state.Branch);
                string changes = state.ChangedFiles > 0 ? $" {state.ChangedFiles} tracked file(s) were changed locally." : "";
                string behind = otherBranch
                    ? $" You chose {SelectedBranch}: run the step again to switch to that branch."
                    : state.Behind > 0 ? $" {state.Behind} new commit(s) are available: run the step again to update." : " It is up to date.";
                Detected.Show(otherBranch || state.Behind > 0 ? ResultKind.Info : ResultKind.Success,
                    $"{_name} is already cloned in {_target}",
                    $"{state.Branch} at {state.Commit}.{behind}{changes}");
                return !otherBranch;
            case CloneTargetKind.OtherContent:
                Detected.Show(ResultKind.Warning, $"{_target} already has other content",
                    (state.Origin is null ? "It is not a git clone." : $"It is a clone of {state.Origin}.") +
                    " Cloning asks first: move it aside to a .bak folder, or abort.");
                return false;
            default:
                Detected.Show(ResultKind.Info, $"{_target} does not exist yet",
                    $"Clone {_name} ({SelectedBranch}) now, or skip this step.");
                return false;
        }
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        string branch = SelectedBranch;
        Run.Log($"> Checking {_target} ...");
        CloneState state = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, _url, _target));
        if (state.Kind == CloneTargetKind.OtherContent)
        {
            Run.Log(state.Origin is null ? "  The folder has files, but is not a git clone." : $"  The folder is a clone of {state.Origin}.");
            Run.Busy = false;
            ExistingFolderChoice choice = await ChoiceDialog.ShowAsync(owner, "Folder not empty",
                $"{_target} already has other content",
                state.Origin is null
                    ? $"It is not a clone of {_name}. It has to make room for the clone."
                    : $"It is a clone of {state.Origin}, not of {_name}.",
                ExistingFolderChoice.Abort,
                new ChoiceDialog.Option<ExistingFolderChoice>("Move it aside and clone",
                    $"{_target} is renamed to {_target}.bak-<date>; nothing is deleted.", ExistingFolderChoice.MoveAside, Primary: true),
                new ChoiceDialog.Option<ExistingFolderChoice>("Abort installation", "Changes nothing on the server and disconnects.",
                    ExistingFolderChoice.Abort));
            Run.Busy = true;
            if (choice == ExistingFolderChoice.Abort) return Aborted(connection);
            string backup = await Task.Run(() => GitCloneInstaller.MoveAsideAsync(connection, _target, Run.Log));
            Run.Done($"moved to {backup}");
        }
        else if (state.Kind == CloneTargetKind.SameRepository)
        {
            Run.Log($"  {_name} is already cloned here ({state.Branch} at {state.Commit}).");
            Run.Done(SameBranch(state.Branch) ? "already cloned: update" : $"already cloned: {state.Branch} -> {branch}");
        }
        else
        {
            Run.Log("  The folder does not exist yet.");
            Run.Done("empty");
        }

        string commit = state.Kind == CloneTargetKind.SameRepository
            ? await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, _target, branch, Run.Log))
            : await Task.Run(() => GitCloneInstaller.CloneAsync(connection, _url, _target, branch, Run.Log));
        Run.Done(state.Kind == CloneTargetKind.SameRepository ? "git pull" : "git clone");

        CloneState after = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, _url, _target));
        if (after.Kind != CloneTargetKind.SameRepository)
            throw new ServerConnectionException($"{_target} is not a clone of {_name} after cloning.");
        _clonedBranch = after.Branch;
        ShowBranchNote();
        Run.Log($"  {_target}: {commit}");
        Run.Done(commit);

        return await SucceededAsync($"{_name} is in {_target}", $"Branch {commit}.");
    }
}
