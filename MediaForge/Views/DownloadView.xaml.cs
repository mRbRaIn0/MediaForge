using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class DownloadView
{
    private const int MaxVisibleLogLines = 1200;
    private DownloadViewModel? _boundViewModel;
    private bool _showsEmptyLog;

    public DownloadView()
    {
        InitializeComponent();
        DataContextChanged += DownloadView_DataContextChanged;
        Loaded += DownloadView_Loaded;
        Unloaded += DownloadView_Unloaded;
    }

    private void DownloadView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null) return;
        Unsubscribe(_boundViewModel);
        Subscribe(_boundViewModel);
        RenderLog();
    }

    private void DownloadView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundViewModel is not null) Unsubscribe(_boundViewModel);
        _boundViewModel = e.NewValue as DownloadViewModel;
        if (_boundViewModel is not null) Subscribe(_boundViewModel);
        RenderLog();
    }

    private void DownloadView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is not null) Unsubscribe(_boundViewModel);
    }

    private void Subscribe(DownloadViewModel viewModel)
    {
        viewModel.LogLinesAppended += ViewModel_LogLinesAppended;
        viewModel.LogCleared += ViewModel_LogCleared;
    }

    private void Unsubscribe(DownloadViewModel viewModel)
    {
        viewModel.LogLinesAppended -= ViewModel_LogLinesAppended;
        viewModel.LogCleared -= ViewModel_LogCleared;
    }

    private void ViewModel_LogLinesAppended(object? sender, IReadOnlyList<string> lines)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ViewModel_LogLinesAppended(sender, lines));
            return;
        }
        if (_showsEmptyLog) { LogBox.Document.Blocks.Clear(); _showsEmptyLog = false; }
        foreach (var line in lines) AddLogLine(line.TrimEnd('\r', '\n'));
        while (LogBox.Document.Blocks.Count > MaxVisibleLogLines)
        {
            var first = LogBox.Document.Blocks.FirstBlock;
            if (first is null) break;
            LogBox.Document.Blocks.Remove(first);
        }
        LogBox.ScrollToEnd();
    }

    private void ViewModel_LogCleared(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ViewModel_LogCleared(sender, e));
            return;
        }
        RenderLog();
    }

    private void RenderLog()
    {
        LogBox.Document.Blocks.Clear();
        var lines = (_boundViewModel?.LogText ?? string.Empty)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            _showsEmptyLog = true;
            AddLogLine("Keine Ausgabe.", Color.FromRgb(115, 126, 142));
        }
        else
        {
            _showsEmptyLog = false;
            foreach (var line in lines) AddLogLine(line);
        }
        LogBox.ScrollToEnd();
    }

    private void AddLogLine(string line, Color? overrideColor = null)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 17 };
        if (overrideColor is { } color)
        {
            paragraph.Inlines.Add(ColoredRun(line, color));
        }
        else if (line.Contains("System Ready", StringComparison.Ordinal))
        {
            AddSystemReadyLine(paragraph, line);
        }
        else
        {
            var lineColor = line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[CRIT]", StringComparison.Ordinal)
                ? Color.FromRgb(225, 99, 99)
                : line.Contains("[WARN]", StringComparison.Ordinal) || line.Contains("[WAIT]", StringComparison.Ordinal)
                    ? Color.FromRgb(224, 184, 74)
                    : line.Contains("[FINISH]", StringComparison.Ordinal) || line.Contains("[UPDATE]", StringComparison.Ordinal)
                        ? Color.FromRgb(76, 175, 125)
                        : line.Contains("[DL]", StringComparison.Ordinal) || line.Contains("[IMPORT]", StringComparison.Ordinal)
                            ? Color.FromRgb(74, 180, 224)
                            : Color.FromRgb(197, 204, 214);
            paragraph.Inlines.Add(ColoredRun(line, lineColor));
        }
        LogBox.Document.Blocks.Add(paragraph);
    }

    private static void AddSystemReadyLine(Paragraph paragraph, string line)
    {
        const string ready = "System Ready";
        const string location = "Speicherort: ";
        var readyIndex = line.IndexOf(ready, StringComparison.Ordinal);
        var pathIndex = line.IndexOf(location, StringComparison.Ordinal);
        if (readyIndex < 0 || pathIndex < readyIndex)
        {
            paragraph.Inlines.Add(ColoredRun(line, Color.FromRgb(197, 204, 214)));
            return;
        }

        paragraph.Inlines.Add(ColoredRun(line[..readyIndex], Color.FromRgb(197, 204, 214)));
        paragraph.Inlines.Add(ColoredRun(ready, Color.FromRgb(76, 175, 125)));
        var pathStart = pathIndex + location.Length;
        paragraph.Inlines.Add(ColoredRun(line[(readyIndex + ready.Length)..pathStart], Color.FromRgb(197, 204, 214)));
        var suffixIndex = line.LastIndexOf(" ---", StringComparison.Ordinal);
        if (suffixIndex < pathStart) suffixIndex = line.Length;
        paragraph.Inlines.Add(ColoredRun(line[pathStart..suffixIndex], Color.FromRgb(74, 180, 224)));
        if (suffixIndex < line.Length) paragraph.Inlines.Add(ColoredRun(line[suffixIndex..], Color.FromRgb(197, 204, 214)));
    }

    private static Run ColoredRun(string text, Color color) => new(text) { Foreground = new SolidColorBrush(color) };

    private void FocusUrl_Click(object sender, RoutedEventArgs e)
    {
        UrlEntry.Focus();
        UrlEntry.SelectAll();
    }

    private void ShowErrors_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadViewModel vm || vm.FailedCount == 0) return;
        var window = new ErrorWindow(vm) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
