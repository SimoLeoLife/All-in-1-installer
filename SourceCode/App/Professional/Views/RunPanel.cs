using Avalonia.Collections;
using Avalonia.Controls;
using static AllInOneInstaller.App.Professional.ProfessionalTheme;

namespace AllInOneInstaller.App.Professional.Views;

/// <summary>What an install step shows while it runs: progress bar, check list, result box and the live log.</summary>
public sealed class RunPanel : StackPanel
{
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, Foreground = Solid(Brand) };
    private readonly AvaloniaList<string> _lines = [];
    private readonly LogView _log;
    private int _stage;

    public CheckList Checks { get; }
    public ResultBanner Result { get; } = new();

    public RunPanel(double logHeight, params string[] checks)
    {
        Spacing = 14;
        Checks = new CheckList(checks);
        _log = new LogView(_lines) { Height = logHeight };
        Children.Add(_progress);
        Children.Add(Checks);
        Children.Add(Result);
        Children.Add(_log);
    }

    public bool Busy
    {
        get => _progress.IsVisible;
        set => _progress.IsVisible = value;
    }

    /// <summary>Index of the check that is running now.</summary>
    public int Stage => _stage;

    public void Start()
    {
        _lines.Clear();
        _log.ResetFollow();
        Result.Hide();
        Checks.Reset();
        _stage = 0;
        Busy = true;
    }

    /// <summary>Adds a log line. Safe to call from any thread.</summary>
    public void Log(string line)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(line));
            return;
        }
        _lines.Add(line);
        _log.FollowOutput();
    }

    /// <summary>Marks the running check as done and moves on to the next one.</summary>
    public void Done(string detail = "")
    {
        Checks.SetDetail(_stage, detail);
        Checks.Update(++_stage, failed: false);
    }

    public void Fail() => Checks.Update(_stage, failed: true);
}
