using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MediaForge.Core;

/// <summary>
/// Bündelt Wiedergabe, Positionstimer und Sichtbarkeit eines <see cref="MediaElement"/>,
/// damit Ansichten mit Vorschau nur noch Play/Pause/Stop aufrufen müssen.
/// </summary>
public sealed class PlayerController
{
    private readonly MediaElement _player;
    private readonly DispatcherTimer _timer;
    private readonly Action<double> _onPosition;
    private readonly Action? _onStateChanged;

    public PlayerController(MediaElement player, Action<double> onPosition, Action? onStateChanged = null)
    {
        _player = player;
        _onPosition = onPosition;
        _onStateChanged = onStateChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => _onPosition(_player.Position.TotalSeconds);
        _player.MediaEnded += (_, _) => Stop();
    }

    public bool IsPlaying { get; private set; }
    public bool HasSource => _player.Source is not null;

    public void SetSource(string? path)
    {
        Stop();
        _player.Source = string.IsNullOrEmpty(path) ? null : new Uri(path);
    }

    public void Play(double seconds, double speed = 1)
    {
        if (_player.Source is null) return;
        _player.Visibility = Visibility.Visible;
        _player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        _player.SpeedRatio = speed;
        _player.Play();
        _timer.Start();
        IsPlaying = true;
        _onStateChanged?.Invoke();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _player.Pause();
        _timer.Stop();
        IsPlaying = false;
        _onStateChanged?.Invoke();
    }

    /// <summary>Startet ab <paramref name="seconds"/> oder pausiert, je nach aktuellem Zustand.</summary>
    public void Toggle(double seconds)
    {
        if (IsPlaying) Pause();
        else Play(seconds);
    }

    public void Seek(double seconds)
    {
        if (_player.Source is not null) _player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    public void Stop()
    {
        _player.Stop();
        _player.Visibility = Visibility.Collapsed;
        _timer.Stop();
        if (!IsPlaying) return;
        IsPlaying = false;
        _onStateChanged?.Invoke();
    }
}
