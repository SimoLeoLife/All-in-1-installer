using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>A list of steps that are ticked off as they finish.</summary>
public sealed class CheckList : StackPanel
{
    private readonly string[] _labels;
    private readonly string[] _details;

    public CheckList(params string[] labels)
    {
        _labels = labels;
        _details = new string[labels.Length];
        Spacing = 8;
        Update(0, failed: false);
    }

    public int Count => _labels.Length;

    public void SetDetail(int index, string detail) => _details[index] = detail;

    /// <summary>Marks the steps before <paramref name="running"/> as done and that step as busy or failed.</summary>
    public void Update(int running, bool failed)
    {
        Children.Clear();
        for (int i = 0; i < _labels.Length; i++)
        {
            bool done = i < running, current = i == running;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
            (string icon, string color) = done ? (Icons.Check, "Pro.SuccessText")
                : current && failed ? (Icons.Close, "Pro.ErrorText")
                : current ? (Icons.ChevronRight, "Pro.AccentText")
                : (Icons.ChevronRight, "Pro.Subtle");
            row.Children.Add(Icon(icon, 16).Bind(PathIcon.ForegroundProperty, color));
            var label = Text(_labels[i], 13.5, done || current ? "Pro.Text" : "Pro.Muted",
                current && !failed ? FontWeight.SemiBold : FontWeight.Normal);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (done && !string.IsNullOrEmpty(_details[i]))
            {
                var detail = Text(_details[i], 12.5, "Pro.Muted");
                Grid.SetColumn(detail, 2);
                row.Children.Add(detail);
            }
            Children.Add(row);
        }
    }

    public void Reset()
    {
        Array.Clear(_details);
        Update(0, failed: false);
    }
}

public enum ResultKind { Success, Info, Warning, Error }

/// <summary>The coloured outcome box under a check list.</summary>
public sealed class ResultBanner : Border
{
    public ResultBanner()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(16, 12);
        IsVisible = false;
    }

    public void Show(ResultKind kind, string heading, string message)
    {
        (string icon, string fore, string back) = kind switch
        {
            ResultKind.Success => (Icons.Shield, "Pro.SuccessText", "Pro.SuccessBack"),
            ResultKind.Info => (Icons.Info, "Pro.InfoText", "Pro.InfoBack"),
            ResultKind.Warning => (Icons.Alert, "Pro.WarningText", "Pro.WarningBack"),
            _ => (Icons.Alert, "Pro.ErrorText", "Pro.ErrorBack")
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(Icon(icon, 20).Bind(PathIcon.ForegroundProperty, fore));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Text(heading, 14, fore, FontWeight.SemiBold));
        text.Children.Add(Text(message, 12.5));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Child = grid;
        this.Bind(BackgroundProperty, back);
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;
}
