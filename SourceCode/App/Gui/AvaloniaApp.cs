using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
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
        // The SQL editor's result grids.
        Styles.Add(new StyleInclude(new Uri("avares://All-in-1 Installer/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        ProfessionalTheme.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new InstallerWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
