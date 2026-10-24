using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Asks for a password, twice when <c>confirm</c> is set. Returns null when cancelled.</summary>
public static class PasswordDialog
{
    public static async Task<string?> ShowAsync(Window owner, string windowTitle, string heading, string message, bool confirm,
        int minimumLength = 1)
    {
        var dialog = new Window
        {
            Title = windowTitle,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        string? result = null;

        var password = new TextBox { PasswordChar = '•', PlaceholderText = "Password", FontSize = 14, Padding = new Thickness(12, 9) };
        var again = new TextBox { PasswordChar = '•', PlaceholderText = "The same password again", FontSize = 14, Padding = new Thickness(12, 9) };
        TextBlock error = Text("", 12.5, "Pro.ErrorText");
        var ok = new Button { Content = "OK", Padding = new Thickness(22, 9), Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 9), Classes = { "secondary" } };

        void Accept()
        {
            string text = password.Text ?? "";
            if (text.Length < minimumLength) { error.Text = $"Use at least {minimumLength} characters."; return; }
            if (confirm && text != again.Text) { error.Text = "The two passwords are not the same."; return; }
            result = text;
            dialog.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close();
        foreach (TextBox box in new[] { password, again })
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Accept();
            };

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(26, 24) };
        panel.Children.Add(Text(heading, 18, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(message, 13));
        panel.Children.Add(password);
        if (confirm) panel.Children.Add(again);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) => password.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }
}
