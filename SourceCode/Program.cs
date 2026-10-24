using Avalonia;
using AllInOneInstaller.App.Gui;

namespace AllInOneInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(argv);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AvaloniaApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
