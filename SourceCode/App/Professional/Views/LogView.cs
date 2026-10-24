using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// Virtualized log: only visible lines are laid out, so thousands of lines keep the window responsive.
/// Follows new output unless the user scrolled up.
/// </summary>
public sealed class LogView : Border
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    private readonly AvaloniaList<string> _lines;
    private readonly ListBox _list;
    private readonly TextBlock _placeholder;
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
        // Only the user stops following: wheel up, dragging the scroll bar, or keys that move up.
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
        lines.CollectionChanged += (_, _) => _placeholder.IsVisible = _lines.Count == 0;

        var grid = new Grid();
        grid.Children.Add(_list);
        grid.Children.Add(_placeholder);
        Child = grid;
    }

    /// <summary>Scrolls to the newest line unless the user scrolled up.</summary>
    public void FollowOutput()
    {
        if (!_follow || _lines.Count == 0 || _scrollPending) return;
        // After layout, so wrapped lines are measured; output that fits stays at the top.
        _scrollPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            AttachScroll();
            if (_follow) _scroll?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Copies the selected lines, or all, to the clipboard; returns the line count.</summary>
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
            // Scrolling back to the bottom resumes following. Moving up only counts while the user drags the
            // scroll bar: the list itself also scrolls while it measures newly added (wrapped) lines.
            if (atEnd) _follow = true;
            else if (_pointerDown && e.OffsetDelta.Y < 0) _follow = false;
            // New lines grew the list while following: stay at the end.
            if (_follow && !atEnd && e.ExtentDelta.Y > 0) FollowOutput();
        };
    }
}

internal static class LogViewExtensions
{
    public static ScrollViewer? FindDescendantScrollViewer(this Control control) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<ScrollViewer>().FirstOrDefault();
}
