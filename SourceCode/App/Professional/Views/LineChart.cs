using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>
/// A rolling line graph: one or more series of the last <see cref="Capacity"/> values, newest on the right, each
/// with a light fill. The top of the graph is <see cref="Maximum"/>, or the largest value when that is 0.
/// </summary>
public sealed class LineChart : Control
{
    public sealed class Series(Color color)
    {
        public Color Color { get; } = color;
        public List<double> Values { get; } = [];
    }

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(GridBrush));

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public int Capacity { get; init; } = 60;
    /// <summary>Fixed top value (100 for percentages); 0 scales to the data.</summary>
    public double Maximum { get; init; }
    public IReadOnlyList<Series> Lines { get; }

    static LineChart() => AffectsRender<LineChart>(GridBrushProperty);

    public LineChart(params Color[] colors)
    {
        Lines = colors.Select(c => new Series(c)).ToArray();
        ClipToBounds = true;
    }

    public void Add(params double[] values)
    {
        for (int i = 0; i < Lines.Count && i < values.Length; i++)
        {
            Lines[i].Values.Add(values[i]);
            if (Lines[i].Values.Count > Capacity) Lines[i].Values.RemoveAt(0);
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        foreach (Series line in Lines) line.Values.Clear();
        InvalidateVisual();
    }

    /// <summary>The value at the top of the graph.</summary>
    public double Top
    {
        get
        {
            if (Maximum > 0) return Maximum;
            double max = Lines.SelectMany(l => l.Values).DefaultIfEmpty(0).Max();
            return max > 0 ? max * 1.15 : 1;
        }
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;
        if (w <= 2 || h <= 2) return;

        var grid = new Pen(GridBrush ?? Brushes.Gray, 1);
        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        double top = Top;
        double step = w / Math.Max(1, Capacity - 1);
        foreach (Series line in Lines)
        {
            if (line.Values.Count < 2) continue;
            double x0 = w - (line.Values.Count - 1) * step;
            Point At(int i) => new(x0 + i * step, h - Math.Clamp(line.Values[i] / top, 0, 1) * (h - 2) - 1);

            var fill = new StreamGeometry();
            using (StreamGeometryContext g = fill.Open())
            {
                g.BeginFigure(new Point(x0, h), true);
                for (int i = 0; i < line.Values.Count; i++) g.LineTo(At(i));
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(line.Color, 0.14), null, fill);

            var stroke = new StreamGeometry();
            using (StreamGeometryContext g = stroke.Open())
            {
                g.BeginFigure(At(0), false);
                for (int i = 1; i < line.Values.Count; i++) g.LineTo(At(i));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(line.Color), 2, lineJoin: PenLineJoin.Round), stroke);
        }
    }
}
