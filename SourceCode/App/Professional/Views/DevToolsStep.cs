using Avalonia.Controls;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Installs Node.js 26 (NodeSource), Composer and Yarn (Corepack).</summary>
public sealed class DevToolsStep : InstallStep
{
    public override string Title => "Node.js";
    public override string Caption => "Composer · Yarn";
    public override string InstallLabel => "Install Node.js, Composer and Yarn";

    public DevToolsStep() : base(Icons.Wrench,
        $"Node.js {DevToolsInstaller.NodeMajor}, Composer and Yarn",
        "The build tools: Octane and the renderer are built with Node.js and Yarn, the CMS uses Composer.",
        [
            $"Adds the NodeSource Node.js {DevToolsInstaller.NodeMajor}.x repository; its signing key is checked against the published fingerprint.",
            $"Installs Node.js {DevToolsInstaller.NodeMajor} with npm.",
            "Installs Composer (the latest stable composer.phar from getcomposer.org), checked against its published SHA-256 checksum.",
            "Installs Corepack and enables Yarn; each project uses the Yarn version in its package.json (Octane: yarn 4)."
        ],
        "Composer needs PHP, so run the PHP step first.",
        [
            "Check the server",
            $"Add the NodeSource Node.js {DevToolsInstaller.NodeMajor} repository",
            $"Install Node.js {DevToolsInstaller.NodeMajor}",
            "Install Composer",
            "Install Yarn (Corepack)",
            "Check the versions"
        ])
    {
    }

    protected override async Task<bool> DetectCoreAsync(ServerConnection connection)
    {
        DevToolsState state = await Task.Run(() => DevToolsInstaller.InspectAsync(connection));
        if (state.IsComplete && state.Shadowing.Count == 0)
        {
            Detected.Show(ResultKind.Success, "Node.js, Composer and Yarn are already installed",
                $"node {state.NodeVersion}, yarn {state.YarnVersion}, composer {state.ComposerVersion}. You can skip this step, or run it again to update.");
            return true;
        }
        if (state.IsComplete)
        {
            // Installing again would not change this: skipping stays the main choice.
            Detected.Show(ResultKind.Warning, "Installed, but older copies come first in the PATH",
                $"Node.js {DevToolsInstaller.NodeMajor}, Composer and Yarn are in /usr/bin, but {string.Join(", ", state.Shadowing)} " +
                $"hide them. Remove those if you do not need them (rm {string.Join(" ", state.Shadowing)}).");
            return true;
        }
        if (!state.PhpInstalled)
            Detected.Show(ResultKind.Warning, "PHP is not installed", "Composer needs PHP: run the PHP step first, or Composer will fail.");
        else if (state.NodeVersion is not null && (state.NodeMajor != DevToolsInstaller.NodeMajor || !state.NodeFromNodeSource))
            Detected.Show(ResultKind.Info, $"node {state.NodeVersion} is installed",
                $"Installing replaces it with Node.js {DevToolsInstaller.NodeMajor} from NodeSource.");
        else
            Detected.Show(ResultKind.Info, "Not installed yet",
                $"Missing: {string.Join(", ", new[] { state.NodeVersion is null ? "Node.js" : null, state.ComposerVersion is null ? "Composer" : null, state.YarnVersion is null ? "Yarn" : null }.OfType<string>())}.");
        return false;
    }

    protected override async Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner)
    {
        // Composer needs PHP: stop before changing anything.
        Run.Log("> Checking the server ...");
        DevToolsState state = await Task.Run(() => DevToolsInstaller.InspectAsync(connection));
        Run.Log($"  node: {state.NodeVersion ?? "not installed"}{(state.NodeVersion is not null && !state.NodeFromNodeSource ? " (not from NodeSource)" : "")}");
        Run.Log($"  yarn: {state.YarnVersion ?? "not installed"}, composer: {state.ComposerVersion ?? "not installed"}");
        if (!state.PhpInstalled)
            throw new ServerConnectionException("Composer needs PHP, which is not installed. Run the PHP step first (or skip this step).");
        Run.Done(state.NodeVersion is null ? "fresh install" : $"updating from node {state.NodeVersion}");

        await Task.Run(() => DevToolsInstaller.AddNodeSourceAsync(connection, Run.Log));
        Run.Done($"node_{DevToolsInstaller.NodeMajor}.x");
        string node = await Task.Run(() => DevToolsInstaller.InstallNodeAsync(connection, Run.Log));
        Run.Done(node);
        await Task.Run(() => DevToolsInstaller.InstallComposerAsync(connection, Run.Log));
        Run.Done("checksum verified");
        await Task.Run(() => DevToolsInstaller.InstallYarnAsync(connection, Run.Log));
        Run.Done("corepack enabled");

        Run.Log("> Versions");
        DevToolsVersions v = await Task.Run(() => DevToolsInstaller.VersionsAsync(connection, Run.Log));
        Run.Done($"node {v.Node} · npm {v.Npm} · yarn {v.Yarn} · composer {v.Composer}");

        if (v.Shadowing.Count > 0)
        {
            Run.Log($"! Older copies come first in root's PATH and hide the installed tools: {string.Join(", ", v.Shadowing)}");
            return await SucceededWithWarningAsync("Installed, but older copies come first in the PATH",
                $"node {v.Node}, npm {v.Npm}, yarn {v.Yarn}, composer {v.Composer} are in /usr/bin, but typing the command runs " +
                $"{string.Join(", ", v.Shadowing)}. Remove those (for example: rm {string.Join(" ", v.Shadowing)}) if you do not need them.");
        }
        return await SucceededAsync("Node.js, Composer and Yarn are installed",
            $"node {v.Node}, npm {v.Npm}, yarn {v.Yarn}, composer {v.Composer}.");
    }
}
