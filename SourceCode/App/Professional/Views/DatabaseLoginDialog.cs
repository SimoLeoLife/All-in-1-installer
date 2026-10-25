using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Asks for a MariaDB account for the SQL editor. Returns null when cancelled.</summary>
public static class DatabaseLoginDialog
{
    public sealed record Login(string User, string Password, int Port);

    public static async Task<Login?> ShowAsync(Window owner, string host, Login initial, string? problem)
    {
        var dialog = new Window
        {
            Title = "MariaDB login",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        Login? result = null;

        var user = new TextBox { Text = initial.User, PlaceholderText = "User", FontSize = 14, Padding = new Thickness(12, 9) };
        var password = new TextBox { Text = initial.Password, PasswordChar = '•', PlaceholderText = "Password", FontSize = 14, Padding = new Thickness(12, 9) };
        var port = new TextBox { Text = initial.Port.ToString(), PlaceholderText = "3306", FontSize = 14, Padding = new Thickness(12, 9), Width = 110 };
        TextBlock error = Text(problem ?? "", 12.5, "Pro.ErrorText");
        error.TextWrapping = TextWrapping.Wrap;
        var ok = new Button { Content = "Connect", Padding = new Thickness(22, 9), Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 9), Classes = { "secondary" } };

        void Accept()
        {
            if (string.IsNullOrWhiteSpace(user.Text)) { error.Text = "Enter a user, such as root."; return; }
            if (!int.TryParse(port.Text, out int number) || number is < 1 or > 65535) { error.Text = "The port is a number from 1 to 65535."; return; }
            result = new Login(user.Text.Trim(), password.Text ?? "", number);
            dialog.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close();
        foreach (TextBox box in new[] { user, password, port })
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
                else if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); }
            };

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(26, 24) };
        panel.Children.Add(Text("Log in to MariaDB", 18, weight: FontWeight.SemiBold));
        var message = Text($"The SQL editor connects through the SSH session to 127.0.0.1 on {host}, like HeidiSQL with an SSH tunnel. " +
                           "The installer's root password is in configuration/database when this computer installed MariaDB.", 13);
        message.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(message);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(user);
        Grid.SetColumn(port, 1);
        row.Children.Add(port);
        panel.Children.Add(row);
        panel.Children.Add(password);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) => (initial.Password.Length == 0 ? password : user).Focus();
        await dialog.ShowDialog(owner);
        return result;
    }
}
