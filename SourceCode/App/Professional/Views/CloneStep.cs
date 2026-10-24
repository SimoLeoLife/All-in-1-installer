using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Clones a hotel repository on the server, or updates an existing clone.</summary>
public sealed class CloneStep : InstallStep
{
    private enum ExistingFolderChoice { MoveAside, Abort }

    private readonly string _url;
    private readonly string _target;
    private readonly string _name;

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
                "Installs git first when the server does not have it.",
                "Already cloned there? Then it is updated with git pull --ff-only; changed files are never overwritten.",
                "Something else in the folder? You choose: move it aside to a .bak folder, or abort. Nothing is deleted."
            ],
            null,
            ["Check the folder", "Clone or update", "Check the clone"])
    {
        Title = title;
        Caption = caption;
        _url = $"https://github.com/{repository}.git";
        _target = target;
        _name = repository.Split('/').Last();
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        CloneState state = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, _url, _target));
        switch (state.Kind)
        {
            case CloneTargetKind.SameRepository:
                string changes = state.ChangedFiles > 0 ? $" {state.ChangedFiles} tracked file(s) were changed locally." : "";
                string behind = state.Behind > 0 ? $" {state.Behind} new commit(s) are available: run the step again to update." : " It is up to date.";
                Detected.Show(state.Behind > 0 ? ResultKind.Info : ResultKind.Success, $"{_name} is already cloned in {_target}",
                    $"{state.Branch} at {state.Commit}.{behind}{changes}");
                return true;
            case CloneTargetKind.OtherContent:
                Detected.Show(ResultKind.Warning, $"{_target} already has other content",
                    (state.Origin is null ? "It is not a git clone." : $"It is a clone of {state.Origin}.") +
                    " Cloning asks first: move it aside to a .bak folder, or abort.");
                return false;
            default:
                Detected.Show(ResultKind.Info, $"{_target} does not exist yet", $"Clone {_name} now, or skip this step.");
                return false;
        }
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
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
        else
        {
            Run.Log(state.Kind == CloneTargetKind.SameRepository
                ? $"  {_name} is already cloned here ({state.Branch} at {state.Commit})."
                : "  The folder does not exist yet.");
            Run.Done(state.Kind == CloneTargetKind.SameRepository ? "already cloned: update" : "empty");
        }

        string commit = state.Kind == CloneTargetKind.SameRepository
            ? await Task.Run(() => GitCloneInstaller.UpdateAsync(connection, _target, Run.Log))
            : await Task.Run(() => GitCloneInstaller.CloneAsync(connection, _url, _target, Run.Log));
        Run.Done(state.Kind == CloneTargetKind.SameRepository ? "git pull" : "git clone");

        CloneState after = await Task.Run(() => GitCloneInstaller.InspectAsync(connection, _url, _target));
        if (after.Kind != CloneTargetKind.SameRepository)
            throw new ServerConnectionException($"{_target} is not a clone of {_name} after cloning.");
        Run.Log($"  {_target}: {commit}");
        Run.Done(commit);

        return await SucceededAsync($"{_name} is in {_target}", $"Branch {commit}.");
    }
}
