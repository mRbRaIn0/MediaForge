using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class SorterView
{
    private readonly DispatcherTimer _timer;
    private SorterViewModel? _viewModel;
    private bool _playing;
    private bool _pauseOnOpen;
    private bool _suppressSeek;

    public SorterView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => UpdatePosition();
    }

    /// <summary>Die Ansicht wird bei jeder Navigation neu erzeugt — Anmeldung deshalb hier statt im Konstruktor.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SorterViewModel viewModel) return;

        _viewModel = viewModel;
        viewModel.ReleaseRequested += ReleasePlayer;
        viewModel.CurrentChanged += ShowItem;
        ShowItem(viewModel.Current);
        Dispatcher.BeginInvoke(() => Keyboard.Focus(this), DispatcherPriority.Input);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleasePlayer();
        if (_viewModel is null) return;
        _viewModel.ReleaseRequested -= ReleasePlayer;
        _viewModel.CurrentChanged -= ShowItem;
        _viewModel = null;
    }

    private void ShowItem(SorterItem? item)
    {
        ReleasePlayer();
        if (item is not { Kind: SorterItemKind.Video }) return;
        try { Player.Source = new Uri(item.FullPath); }
        catch
        {
            _viewModel?.Notify("Video konnte nicht geöffnet werden.", isError: true);
            return;
        }
        // Auch ohne Autoplay kurz starten, damit das erste Bild sichtbar wird.
        _pauseOnOpen = _viewModel?.AutoPlay != true;
        Play();
    }

    private void Play()
    {
        if (Player.Source is null) return;
        Player.Play();
        _timer.Start();
        _playing = true;
        UpdatePlayState();
    }

    private void Pause()
    {
        Player.Pause();
        _timer.Stop();
        _playing = false;
        UpdatePlayState();
    }

    private void ReleasePlayer()
    {
        _timer.Stop();
        _playing = false;
        _pauseOnOpen = false;
        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        catch { /* Player war noch nicht geladen. */ }
        _suppressSeek = true;
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
        _suppressSeek = false;
        TimeText.Text = "00:00:00 / 00:00:00";
        UpdatePlayState();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;
        Pause();
        Player.Position = TimeSpan.Zero;
        UpdatePosition(force: true);
    }

    private void TogglePlay()
    {
        if (Player.Source is null) return;
        if (_playing) { Pause(); return; }
        if (SeekSlider.Maximum > 0 && Player.Position.TotalSeconds >= SeekSlider.Maximum - .15)
            Player.Position = TimeSpan.Zero;
        Play();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _suppressSeek = true;
        SeekSlider.Maximum = Player.NaturalDuration.HasTimeSpan
            ? Math.Max(.1, Player.NaturalDuration.TimeSpan.TotalSeconds)
            : 1;
        SeekSlider.Value = 0;
        _suppressSeek = false;
        if (_pauseOnOpen)
        {
            _pauseOnOpen = false;
            Pause();
            Player.Position = TimeSpan.Zero;
        }
        UpdateTime();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Player.Position = TimeSpan.Zero;
        UpdatePosition(force: true);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _pauseOnOpen = false;
        Pause();
        _viewModel?.Notify("Dieses Video kann nicht abgespielt werden — Sortieren geht trotzdem.", isError: true);
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || Player.Source is null) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateTime();
    }

    private void UpdatePosition(bool force = false)
    {
        if (!_playing && !force) return;
        _suppressSeek = true;
        SeekSlider.Value = Math.Clamp(Player.Position.TotalSeconds, 0, SeekSlider.Maximum);
        _suppressSeek = false;
        UpdateTime();
    }

    private void UpdateTime()
    {
        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
        TimeText.Text = $"{Formatters.TimeFromMs((long)Player.Position.TotalMilliseconds)} / {Formatters.TimeFromMs((long)duration)}";
    }

    private void UpdatePlayState()
    {
        PlayGlyph.Text = _playing ? "" : "";
        PlayText.Text = _playing ? "Pause" : "Abspielen";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SorterViewModel viewModel) return;
        if (!viewModel.IsIdle) { e.Handled = true; return; }
        if (Keyboard.FocusedElement is System.Windows.Controls.CheckBox) return;

        if (e.Key == Key.Space)
        {
            TogglePlay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Left)
        {
            Execute(viewModel.PreviousCommand);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Right)
        {
            Execute(viewModel.NextCommand);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            Execute(viewModel.RescanCommand);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (viewModel.UndoCommand.CanExecute(null)) viewModel.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        // Zielbereiche zuerst: eine eigens vergebene Taste hat Vorrang vor den festen Kürzeln.
        if (viewModel.TrySortByKey(KeyText(e.Key)))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S)
        {
            Execute(viewModel.SkipCommand);
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            Execute(viewModel.ReloadCommand);
            e.Handled = true;
        }
    }

    private static void Execute(RelayCommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static string KeyText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        _ => string.Empty
    };

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) _viewModel?.RefreshTools();
    }
}
