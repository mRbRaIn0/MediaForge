using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class SpeedView
{
    private readonly DispatcherTimer _timer;
    private SpeedViewModel? _subscribed;
    private bool _playing;

    public SpeedView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Timer_Tick;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribed = e.NewValue as SpeedViewModel;
        if (_subscribed is not null) { _subscribed.PropertyChanged += ViewModel_PropertyChanged; SetSource(); }
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SpeedViewModel.SourcePath)) SetSource(); }
    private void SetSource() { Stop(); Player.Source = DataContext is SpeedViewModel { SourcePath: not null } vm ? new Uri(vm.SourcePath) : null; }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeedViewModel { SourcePath: not null } vm) return;
        if (_playing) { Player.Pause(); _timer.Stop(); _playing = false; UpdatePlayState(); return; }
        if (vm.Position >= vm.Duration - .05) vm.Position = 0;
        Player.Visibility = Visibility.Visible; Player.Position = TimeSpan.FromSeconds(vm.Position); Player.SpeedRatio = vm.FactorAt(vm.Position); Player.Play(); _timer.Start(); _playing = true; UpdatePlayState();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private async void Timeline_SeekRequested(object? sender, double value)
    {
        if (Player.Source is not null) Player.Position = TimeSpan.FromSeconds(value);
        if (!_playing && DataContext is SpeedViewModel vm) await vm.UpdatePreviewAsync(value);
    }
    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DataContext is not SpeedViewModel vm) return;
        vm.Position = Player.Position.TotalSeconds;
        var ratio = vm.FactorAt(vm.Position);
        if (Math.Abs(Player.SpeedRatio - ratio) > .001) Player.SpeedRatio = ratio;
    }
    private void Player_MediaEnded(object sender, RoutedEventArgs e) => Stop();
    private void Stop()
    {
        Player.Stop(); Player.Visibility = Visibility.Collapsed; _timer.Stop(); _playing = false; UpdatePlayState();
        if (DataContext is SpeedViewModel { HasMedia: true } vm) _ = vm.UpdatePreviewAsync(vm.Position);
    }
    private void UpdatePlayState() { PlayGlyph.Text = _playing ? "\uE769" : "\uE768"; PlayText.Text = _playing ? "Pause" : "Vorschau (Tempo)"; }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
}
