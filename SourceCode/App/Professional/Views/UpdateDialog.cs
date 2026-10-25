using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AllInOneInstaller.Update;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// "A new version is available" at startup: what is new, then Update and restart or Later. The download
/// replaces the running program in the same window, so there is nothing to do by hand.
/// </summary>
public static class UpdateDialog
{
    /// <summary>True when the program was replaced and the caller should start the new one and close.</summary>
    public static async Task<bool> ShowAsync(Window owner, AvailableUpdate update)
    {
        var dialog = new Window
        {
            Title = "Update",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        bool replaced = false, busy = false;

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, Margin = new Thickness(26, 24) };
        layout.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Solid(Color.FromArgb(0x26, Brand.R, Brand.G, Brand.B)),
            Child = Icon(Icons.Download, 20).Bind(PathIcon.ForegroundProperty, "Pro.Text")
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text($"Version {update.Version} is available", 18, weight: FontWeight.SemiBold));
        panel.Children.Add(Text($"You are running {AppUpdate.Current}. The new version is downloaded from " +
                                $"github.com/{AppUpdate.Repository} and takes the place of this program; it starts again by itself.", 13));
        if (Notes(update.Notes) is { } notes) panel.Children.Add(Text(notes, 12, "Pro.Muted"));

        var progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, IsVisible = false };
        progress.Foreground = Solid(Brand);
        TextBlock status = Text("", 12, "Pro.Muted");
        status.IsVisible = false;
        panel.Children.Add(progress);
        panel.Children.Add(status);

        var apply = new Button { Content = "Update and restart", Padding = new Thickness(20, 9), Classes = { "primary" } };
        var later = new Button { Content = "Later", Padding = new Thickness(18, 9), Classes = { "secondary" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(later);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);

        later.Click += (_, _) => dialog.Close();
        apply.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            apply.IsEnabled = later.IsEnabled = false;
            progress.IsVisible = status.IsVisible = true;
            status.Text = $"Downloading {update.AssetName} ...";

            var report = new Progress<(long Done, long Total)>(p =>
            {
                progress.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) progress.Value = Math.Clamp(p.Done / (double)p.Total, 0, 1);
                status.Text = $"Downloading {update.AssetName}: {Megabytes(p.Done)} of {Megabytes(p.Total)}";
            });
            try
            {
                await AppUpdate.DownloadAndReplaceAsync(update, report);
                progress.Value = 1;
                status.Text = "Downloaded. Starting the new version ...";
                replaced = true;
                // Let the last line appear before the window goes away.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                dialog.Close();
            }
            catch (Exception ex)
            {
                progress.IsVisible = false;
                status.Bind(TextBlock.ForegroundProperty, "Pro.ErrorText");
                status.Text = $"The update failed: {ex.Message}  Download it yourself from {update.PageUrl}";
                later.Content = "Close";
                later.IsEnabled = true;
                busy = false;
            }
        };
        // Closing the window while the download runs would leave it half done.
        dialog.Closing += (_, e) => e.Cancel = busy;

        Grid.SetColumn(panel, 1);
        layout.Children.Add(panel);
        dialog.Content = layout;
        await dialog.ShowDialog(owner);
        return replaced;
    }

    // The release text, shortened: a dialog is not the place for a full changelog.
    private static string? Notes(string body)
    {
        string notes = body.Replace("\r", "").Trim();
        if (notes.Length == 0) return null;
        return notes.Length > 600 ? notes[..600].TrimEnd() + " ..." : notes;
    }

    private static string Megabytes(long bytes) => bytes <= 0 ? "?" : $"{bytes / (1024.0 * 1024.0):0.0} MB";
}
