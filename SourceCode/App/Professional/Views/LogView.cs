using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>Virtualized log list that follows new output unless the user has scrolled up.</summary>
public sealed class LogView : Border
{
    private const string CopyLabel = "Copy log";

    private readonly AvaloniaList<string> _lines;
    private readonly ListBox _list;
    private readonly TextBlock _placeholder;
    // Floats in the top right corner of the log, so a failure can be pasted somewhere without a screenshot.
    private readonly Button _copy = new()
    {
        Content = CopyLabel,
        Padding = new Thickness(10, 3),
        FontSize = 11,
        Classes = { "secondary" },
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 8, 18, 0),
        Opacity = 0.85
    };
    private readonly DispatcherTimer _copyLabelBack = new() { Interval = TimeSpan.FromSeconds(2) };
    private ScrollViewer? _scroll;
    private bool _follow = true;
    private bool _pointerDown;
    private bool _scrollPending;

    public LogView(AvaloniaList<string> lines)
    {
        _lines = lines;
        Background = Solid(ConsoleBackground);
        BorderBrush = Solid(ConsoleBorder);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;

        _list = new ListBox
        {
            ItemsSource = lines,
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 10),
            ItemTemplate = new FuncDataTemplate<string>((_, _) =>
            {
                var text = new TextBlock { FontFamily = Mono, FontSize = 12, Foreground = Solid(ConsoleText), TextWrapping = TextWrapping.Wrap };
                // Containers are recycled while scrolling, so follow the data context instead of the first item.
                text.DataContextChanged += (_, _) => text.Text = text.DataContext as string;
                return text;
            })
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.Resources["ListBoxItemPadding"] = new Thickness(14, 1);
        _list.Resources["SystemControlHighlightListLowBrush"] = Solid(Color.Parse("#16FFFFFF"));
        _list.Resources["SystemControlHighlightListMediumBrush"] = Solid(Color.Parse("#22FFFFFF"));
        _list.Resources["SystemControlHighlightListAccentLowBrush"] = Solid(Color.Parse("#334F5FE6"));
        _list.Resources["SystemControlHighlightListAccentMediumBrush"] = Solid(Color.Parse("#444F5FE6"));
        _list.Resources["SystemControlHighlightListAccentHighBrush"] = Solid(Color.Parse("#554F5FE6"));
        // Only user input stops following. The scroll bar drag case is handled in AttachScroll.
        _list.AddHandler(PointerWheelChangedEvent, (_, e) => { if (e.Delta.Y > 0) _follow = false; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.AddHandler(PointerPressedEvent, (_, _) => _pointerDown = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, (_, _) => _pointerDown = false, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(KeyDownEvent, (_, e) => { if (e.Key is Key.Up or Key.PageUp or Key.Home) _follow = false; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.C || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
            e.Handled = true;
            await CopyAsync(selectedOnly: true);
        };
        _list.TemplateApplied += (_, _) => AttachScroll();

        _placeholder = new TextBlock
        {
            Text = "Output appears here when the installer connects.",
            FontFamily = Mono,
            FontSize = 12,
            Foreground = Solid(Color.Parse("#6B7890")),
            Margin = new Thickness(14, 12),
            IsHitTestVisible = false
        };
        ToolTip.SetTip(_copy, "Copies the whole log to the clipboard, or only the lines you selected. Ctrl+C does the same.");
        _copy.IsVisible = _lines.Count > 0;
        _copy.Click += async (_, _) =>
        {
            int copied = await CopyAsync(selectedOnly: true);
            _copy.Content = copied > 0 ? $"Copied {copied} line{(copied == 1 ? "" : "s")}" : "Nothing to copy";
            _copyLabelBack.Stop();
            _copyLabelBack.Start();
        };
        _copyLabelBack.Tick += (_, _) =>
        {
            _copyLabelBack.Stop();
            _copy.Content = CopyLabel;
        };
        lines.CollectionChanged += (_, _) =>
        {
            _placeholder.IsVisible = _lines.Count == 0;
            _copy.IsVisible = _lines.Count > 0;
        };

        var grid = new Grid();
        grid.Children.Add(_list);
        grid.Children.Add(_placeholder);
        grid.Children.Add(_copy);
        Child = grid;
    }

    public void FollowOutput()
    {
        if (!_follow || _lines.Count == 0 || _scrollPending) return;
        // Scroll after layout so wrapped lines are measured first.
        _scrollPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            AttachScroll();
            if (_follow) _scroll?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Copies the selected lines, or all lines, and returns how many were copied.</summary>
    public async Task<int> CopyAsync(bool selectedOnly)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return 0;

        IEnumerable<int> indexes = selectedOnly && _list.Selection.SelectedIndexes.Count > 0
            ? _list.Selection.SelectedIndexes.Where(i => i < _lines.Count).OrderBy(i => i)
            : Enumerable.Range(0, _lines.Count);
        string[] text = indexes.Select(i => _lines[i]).ToArray();
        await clipboard.SetValueAsync(DataFormat.Text, string.Join(Environment.NewLine, text));
        return text.Length;
    }

    public void ResetFollow()
    {
        _follow = true;
        _list.Selection.Clear();
    }

    private void AttachScroll()
    {
        if (_scroll is not null) return;
        _scroll = _list.FindDescendantScrollViewer();
        if (_scroll is null) return;
        _scroll.ScrollChanged += (_, e) =>
        {
            bool atEnd = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;
            // Scrolling up only counts while the pointer is down, because the list also shifts on its own
            // while it measures newly added wrapped lines.
            if (atEnd) _follow = true;
            else if (_pointerDown && e.OffsetDelta.Y < 0) _follow = false;
            if (_follow && !atEnd && e.ExtentDelta.Y > 0) FollowOutput();
        };
    }
}

internal static class LogViewExtensions
{
    public static ScrollViewer? FindDescendantScrollViewer(this Control control) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<ScrollViewer>().FirstOrDefault();
}
