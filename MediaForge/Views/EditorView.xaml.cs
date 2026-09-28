using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class EditorView
{
    private readonly PlayerController _player;
    private EditorViewModel? _subscribed;

    public EditorView()
    {
        InitializeComponent();
        _player = new PlayerController(Player, OnPlayerPosition, UpdatePlayState);
        DataContextChanged += OnDataContextChanged;
    }

    private EditorViewModel? ViewModel => DataContext as EditorViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribed.SeekRequested -= OnSeekRequested;
            _subscribed.TimelineInvalidated -= OnTimelineInvalidated;
            _subscribed.EditMarkerDialog = null;
        }
        _subscribed = e.NewValue as EditorViewModel;
        if (_subscribed is null) return;
        _subscribed.PropertyChanged += ViewModel_PropertyChanged;
        _subscribed.SeekRequested += OnSeekRequested;
        _subscribed.TimelineInvalidated += OnTimelineInvalidated;
        _subscribed.EditMarkerDialog = OpenMarkerDialog;
        SetSource();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.SourcePath)) SetSource();
    }

    private void SetSource() => _player.SetSource(ViewModel?.SourcePath);

    private void OnTimelineInvalidated() => Timeline.InvalidateVisual();

    private void OnPlayerPosition(double seconds)
    {
        if (ViewModel is { } vm) vm.Position = seconds;
    }

    private void UpdatePlayState()
    {
        PlayGlyph.Text = _player.IsPlaying ? "" : "";
        PlayText.Text = _player.IsPlaying ? "Pause" : "Abspielen";
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SourcePath: not null } vm) return;
        _player.Toggle(vm.Position);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        UpdatePlayState();
        if (ViewModel is { HasMedia: true } vm) _ = vm.UpdatePreviewAsync(vm.Position);
    }

    private void Timeline_SeekRequested(object? sender, double value) => OnSeekRequested(value);

    private void Timeline_MarkerClicked(object? sender, MediaMarker marker)
    {
        if (ViewModel is not { } vm) return;
        vm.SelectedMarker = marker;
        MarkerList.ScrollIntoView(marker);
    }

    private void OnSeekRequested(double seconds)
    {
        _player.Seek(seconds);
        if (!_player.IsPlaying && ViewModel is { HasMedia: true } vm) _ = vm.UpdatePreviewAsync(seconds);
    }

    private void MarkerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm && MarkerList.SelectedItem is MediaMarker marker) vm.JumpToMarkerCommand.Execute(marker);
    }

    private void MarkerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } vm && MarkerList.SelectedItem is MediaMarker marker) vm.EditMarkerCommand.Execute(marker);
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm && ClipList.SelectedItem is EditorClip clip) vm.SeekTo(clip.StartSeconds);
    }

    /// <summary>Öffnet das Marker-Popup; die Wiedergabe pausiert dabei.</summary>
    private MarkerDialogResult OpenMarkerDialog(MediaMarker marker, bool isNew)
    {
        _player.Pause();
        var window = new MarkerWindow(marker, isNew, ViewModel?.DurationMs ?? 0) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        return window.Result;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.OriginalSource is TextBox or ComboBox) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        switch (e.Key)
        {
            case Key.M:
                var command = shift ? vm.AddRangeMarkerCommand : vm.AddPointMarkerCommand;
                if (command.CanExecute(null)) command.Execute(null);
                break;
            case Key.S:
                if (vm.SplitClipCommand.CanExecute(null)) vm.SplitClipCommand.Execute(null);
                break;
            case Key.Delete:
                if (vm.RemoveClipCommand.CanExecute(null)) vm.RemoveClipCommand.Execute(null);
                break;
            case Key.Space:
                Play_Click(sender, e);
                break;
            case Key.Left:
                vm.SeekTo(vm.Position - (shift ? 5 : 1));
                break;
            case Key.Right:
                vm.SeekTo(vm.Position + (shift ? 5 : 1));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        UpdatePlayState();
    }
}
