using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Asks for a file or folder name. Returns null when cancelled.</summary>
public static class NameDialog
{
    public static async Task<string?> ShowAsync(Window owner, string heading, string message, string initial, string ok,
        Func<string, string?> validate)
    {
        var dialog = new Window
        {
            Title = heading,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        string? result = null;

        var name = new TextBox { Text = initial, FontSize = 14, Padding = new Thickness(12, 9) };
        TextBlock error = Text("", 12.5, "Pro.ErrorText");
        var accept = new Button { Content = ok, Padding = new Thickness(22, 9), Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 9), Classes = { "secondary" } };

        void Accept()
        {
            string text = (name.Text ?? "").Trim();
            if (validate(text) is { } problem) { error.Text = problem; return; }
            result = text;
            dialog.Close();
        }
        accept.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close();
        name.TextChanged += (_, _) => error.Text = "";
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
            else if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); }
        };

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(26, 24) };
        panel.Children.Add(Text(heading, 18, weight: FontWeight.SemiBold));
        var text = Text(message, 13);
        text.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(text);
        panel.Children.Add(name);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) =>
        {
            name.Focus();
            // Select the name without its extension, as file managers do.
            int dot = initial.LastIndexOf('.');
            name.SelectionStart = 0;
            name.SelectionEnd = dot > 0 ? dot : initial.Length;
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
