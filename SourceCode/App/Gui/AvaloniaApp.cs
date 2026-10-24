using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using AllInOneInstaller.App.Professional;
using AllInOneInstaller.App.Professional.Views;

namespace AllInOneInstaller.App.Gui;

/// <summary>Fluent theme plus the Professional styles shared with the All-in-1 Converter.</summary>
public sealed class AvaloniaApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        ProfessionalTheme.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new InstallerWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
