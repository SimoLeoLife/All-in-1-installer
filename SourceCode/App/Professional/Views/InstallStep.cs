using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

public enum StepOutcome { Done, Skipped, Failed, Aborted }

/// <summary>
/// Base for install steps: an intro with what is installed already, and a run panel with a check list
/// and the live log. Subclasses only detect and install.
/// </summary>
public abstract class InstallStep
{
    private readonly ContentControl _host = new();
    private readonly Control _intro;

    /// <summary>Above the bullet list: what <see cref="DetectAsync"/> found, or that the step was skipped.</summary>
    protected ResultBanner Detected { get; } = new();
    protected RunPanel Run { get; }

    /// <summary>Short name for the stepper and messages, e.g. "NGINX".</summary>
    public abstract string Title { get; }
    /// <summary>Second line in the stepper, e.g. "Web server".</summary>
    public abstract string Caption { get; }
    public abstract string InstallLabel { get; }
    /// <summary>Install button text when the step is already done.</summary>
    public virtual string RerunLabel => "Reinstall / upgrade";

    public Control View => _host;
    public event Action<string>? Status;

    protected InstallStep(string icon, string heading, string description, string[] bullets, string? footnote, string[] checks)
    {
        Run = new RunPanel(240, checks);
        _intro = BuildIntro(icon, heading, description, bullets, footnote);
        _host.Content = _intro;
    }

    private Control BuildIntro(string iconData, string heading, string description, string[] bullets, string? footnote)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18 };
        var icon = Icon(iconData, 22);
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

        var panel = new StackPanel { Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Text(heading, 19, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(description, 13, "Pro.Muted"));
        panel.Children.Add(Detected);
        foreach (string line in bullets)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            row.Children.Add(Icon(Icons.Check, 15).Bind(PathIcon.ForegroundProperty, "Pro.SuccessText"));
            var text = Text(line, 13);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        if (footnote is not null) panel.Children.Add(Text(footnote, 12, "Pro.Muted"));
        if (BuildOptions() is { } options) panel.Children.Add(options);

        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    public void ShowIntro() => _host.Content = _intro;

    /// <summary>Settings shown under the bullets. Called from the base constructor, so only field initializers have run.</summary>
    protected virtual Control? BuildOptions() => null;

    /// <summary>Checks those settings before the step runs; returns the problem, or null.</summary>
    public virtual string? Validate() => null;

    public void ShowSkipped()
    {
        _host.Content = _intro;
        Detected.Show(ResultKind.Info, $"{Title} step skipped", $"Nothing was changed on the server for {Title}.");
        SetStatus($"{Title} step skipped.");
    }

    /// <summary>Shows what is installed now. True when everything this step sets up is already there.</summary>
    public async Task<bool> DetectAsync(ServerConnection connection)
    {
        try
        {
            return await DetectCoreAsync(connection);
        }
        catch (ServerConnectionException ex)
        {
            Detected.Show(ResultKind.Warning, $"Could not check {Title}", ex.Message);
            return false;
        }
    }

    public async Task<StepOutcome> RunAsync(ServerConnection connection, Window owner)
    {
        _host.Content = Run;
        Run.Start();
        try
        {
            return await RunCoreAsync(connection, owner);
        }
        catch (Exception ex)
        {
            string message = ex is ServerConnectionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            await FlushLogAsync();
            Run.Log("! " + message);
            Run.Fail();
            Run.Result.Show(ResultKind.Error, $"Installing {Title} failed", message);
            SetStatus($"Installing {Title} failed.");
            return StepOutcome.Failed;
        }
        finally
        {
            Run.Busy = false;
        }
    }

    protected abstract Task<bool> DetectCoreAsync(ServerConnection connection);

    /// <summary>Runs the checks of <see cref="Run"/> in order; throw <see cref="ServerConnectionException"/> to fail.</summary>
    protected abstract Task<StepOutcome> RunCoreAsync(ServerConnection connection, Window owner);

    protected async Task<StepOutcome> SucceededAsync(string heading, string message)
    {
        await FlushLogAsync();
        Run.Result.Show(ResultKind.Success, heading, message);
        SetStatus(heading + ".");
        return StepOutcome.Done;
    }

    /// <summary>Done, but with something the user should fix (shown as a warning).</summary>
    protected async Task<StepOutcome> SucceededWithWarningAsync(string heading, string message)
    {
        await FlushLogAsync();
        Run.Result.Show(ResultKind.Warning, heading, message);
        SetStatus(heading + ".");
        return StepOutcome.Done;
    }

    protected StepOutcome Aborted(ServerConnection connection)
    {
        Run.Log("! Installation aborted by the user. Nothing was changed on the server.");
        Run.Fail();
        Run.Result.Show(ResultKind.Warning, "Installation aborted", $"Nothing was changed on {connection.Login.Host}.");
        SetStatus("Installation aborted.");
        return StepOutcome.Aborted;
    }

    protected void SetStatus(string message) => Status?.Invoke(message);

    // SSH-thread log lines are posted; let them land before the result is shown.
    private static Task FlushLogAsync() =>
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background).GetTask();
}
