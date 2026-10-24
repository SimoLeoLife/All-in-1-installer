using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AllInOneInstaller.Remote;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

public enum ExistingKeysChoice { Replace, Add, Abort }

/// <summary>authorized_keys already holds keys: replace them, add the new key, or abort (also on close).</summary>
public static class ExistingKeysDialog
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    public static async Task<ExistingKeysChoice> ShowAsync(Window owner, IReadOnlyList<AuthorizedKey> existing, AuthorizedKey newKey)
    {
        var dialog = new Window
        {
            Title = "SSH keys already present",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        ExistingKeysChoice result = ExistingKeysChoice.Abort;

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, Margin = new Thickness(26, 24) };
        var badge = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Icon(Icons.Key, 20).Bind(PathIcon.ForegroundProperty, "Pro.WarningText")
        }.Bind(Border.BackgroundProperty, "Pro.WarningBack");
        layout.Children.Add(badge);

        bool alreadyThere = existing.Any(k => k.SameKey(newKey));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Some keys are already present", 18, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(
            $"{RootAuthorizedKeys.FilePath} on the server already holds {existing.Count} key{(existing.Count == 1 ? "" : "s")}" +
            (alreadyThere ? ", including the key you are installing." : ".") + " What do you want to do?", 13));

        var list = new StackPanel { Spacing = 6 };
        foreach (AuthorizedKey key in existing)
        {
            var row = new StackPanel { Spacing = 1 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(Text(key.Comment.Length > 0 ? key.Comment : "(no comment)", 12.5, weight: FontWeight.SemiBold));
            title.Children.Add(Chip(key.Type.Replace("ssh-", "").ToUpperInvariant(), "Pro.AccentSoft", "Pro.AccentText"));
            if (key.SameKey(newKey)) title.Children.Add(Chip("THIS KEY", "Pro.SuccessBack", "Pro.SuccessText"));
            row.Children.Add(title);
            var fingerprint = Text(key.Fingerprint, 11.5, "Pro.Muted");
            fingerprint.FontFamily = Mono;
            row.Children.Add(fingerprint);
            list.Children.Add(row);
        }
        panel.Children.Add(new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { MaxHeight = 180, Content = list }
        }.Bind(Border.BackgroundProperty, "Pro.Surface").Bind(Border.BorderBrushProperty, "Pro.CardBorder"));

        panel.Children.Add(Option(dialog, "Clean all keys and place this one",
            "Every key above is removed; only the new key can log in as root. A backup of the file is kept on the server.",
            "secondary", () => result = ExistingKeysChoice.Replace));
        panel.Children.Add(Option(dialog, "Add my key to it",
            alreadyThere ? "Keeps all keys. Your key is already there, so nothing changes." : "Keeps all keys and adds the new one, so there are multiple keys.",
            // The safe choice is the highlighted one; removing keys can lock other people out.
            "primary", () => result = ExistingKeysChoice.Add));
        panel.Children.Add(Option(dialog, "Abort installation",
            "Changes nothing on the server and disconnects.",
            "secondary", () => result = ExistingKeysChoice.Abort));

        Grid.SetColumn(panel, 1);
        layout.Children.Add(panel);
        dialog.Content = layout;
        await dialog.ShowDialog(owner);
        return result;
    }

    private static Button Option(Window dialog, string title, string description, string style, Action choose)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        var detail = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        content.Children.Add(detail);
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            Classes = { style }
        };
        button.Click += (_, _) =>
        {
            choose();
            dialog.Close();
        };
        return button;
    }
}
