using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class CutView
{
    private readonly DispatcherTimer _timer;
    private bool _playing;
    private CutViewModel? _subscribed;

    public CutView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Timer_Tick;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribed = e.NewValue as CutViewModel;
        if (_subscribed is not null) { _subscribed.PropertyChanged += ViewModel_PropertyChanged; SetSource(); }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CutViewModel.SourcePath)) SetSource();
    }

    private void SetSource()
    {
        Stop();
        Player.Source = DataContext is CutViewModel { SourcePath: not null } vm ? new Uri(vm.SourcePath) : null;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CutViewModel { SourcePath: not null } vm) return;
        if (_playing) { Player.Pause(); _timer.Stop(); _playing = false; UpdatePlayState(); return; }
        if (vm.Position >= vm.OutPoint - .05) vm.Position = vm.InPoint;
        Player.Visibility = Visibility.Visible; Player.Position = TimeSpan.FromSeconds(vm.Position); Player.SpeedRatio = 1; Player.Play(); _timer.Start(); _playing = true; UpdatePlayState();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private async void Timeline_SeekRequested(object? sender, double value)
    {
        if (Player.Source is not null) Player.Position = TimeSpan.FromSeconds(value);
        if (!_playing && DataContext is CutViewModel vm) await vm.UpdatePreviewAsync(value);
    }
    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DataContext is not CutViewModel vm) return;
        vm.Position = Player.Position.TotalSeconds;
        if (vm.Position >= vm.OutPoint) Stop();
    }
    private void Player_MediaEnded(object sender, RoutedEventArgs e) => Stop();
    private void Stop()
    {
        Player.Stop(); Player.Visibility = Visibility.Collapsed; _timer.Stop(); _playing = false; UpdatePlayState();
        if (DataContext is CutViewModel { HasMedia: true } vm) _ = vm.UpdatePreviewAsync(vm.Position);
    }
    private void UpdatePlayState() { PlayGlyph.Text = _playing ? "\uE769" : "\uE768"; PlayText.Text = _playing ? "Pause" : "Abspielen"; }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CutViewModel vm) return;
        if (e.Key == Key.I && vm.SetInCommand.CanExecute(null)) vm.SetInCommand.Execute(null);
        else if (e.Key == Key.O && vm.SetOutCommand.CanExecute(null)) vm.SetOutCommand.Execute(null);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
}
