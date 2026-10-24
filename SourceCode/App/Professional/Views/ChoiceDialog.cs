using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>A question with a few described answers, one per button. Closing the window returns <c>cancel</c>.</summary>
public static class ChoiceDialog
{
    public sealed record Option<T>(string Title, string Description, T Value, bool Primary = false);

    public static async Task<T> ShowAsync<T>(Window owner, string windowTitle, string heading, string message,
        T cancel, params Option<T>[] options)
    {
        var dialog = new Window
        {
            Title = windowTitle,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Bind(Window.BackgroundProperty, "Pro.Card");
        T result = cancel;

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, Margin = new Thickness(26, 24) };
        layout.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Icon(Icons.Alert, 20).Bind(PathIcon.ForegroundProperty, "Pro.WarningText")
        }.Bind(Border.BackgroundProperty, "Pro.WarningBack"));

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(heading, 18, weight: FontWeight.SemiBold));
        panel.Children.Add(Text(message, 13));
        foreach (Option<T> option in options)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = option.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = option.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                CornerRadius = new CornerRadius(8),
                Classes = { option.Primary ? "primary" : "secondary" }
            };
            button.Click += (_, _) =>
            {
                result = option.Value;
                dialog.Close();
            };
            panel.Children.Add(button);
        }
        Grid.SetColumn(panel, 1);
        layout.Children.Add(panel);
        dialog.Content = layout;
        await dialog.ShowDialog(owner);
        return result;
    }
}
