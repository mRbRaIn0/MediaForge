using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class GalleryView : UserControl
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private bool _playing;

    public GalleryView() => InitializeComponent();

    private GalleryViewModel? Model => DataContext as GalleryViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        if (Model is { } model) await model.EnsureLoadedAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopViewer();

    /// <summary>Esc schließt die Einzelansicht, die Pfeiltasten blättern darin.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.OriginalSource is TextBox && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.Escape when model.IsViewerOpen:
                CloseViewer();
                e.Handled = true;
                break;
            case Key.Left when model.IsViewerOpen:
                model.ViewerPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when model.IsViewerOpen:
                model.ViewerNextCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Am unteren Ende der Zeitleiste die nächste Seite nachladen.</summary>
    private void OnTimelineScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (Model is not { HasMore: true } model) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 600) model.AppendPage();
    }

    private void ViewerPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        ViewerPlayer.Play();
        _playing = true;
        ViewerToggleGlyph.Text = PauseGlyph;
    }

    private void ViewerPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        ViewerPlayer.Stop();
        _playing = false;
        ViewerToggleGlyph.Text = PlayGlyph;
    }

    private void ViewerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerPlayer.Source is null) return;
        if (_playing) { ViewerPlayer.Pause(); ViewerToggleGlyph.Text = PlayGlyph; }
        else { ViewerPlayer.Play(); ViewerToggleGlyph.Text = PauseGlyph; }
        _playing = !_playing;
    }

    private void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();

    /// <summary>Beim Aufklappen des Umbenennfelds den Text auswählen — die Eingabe soll sofort losgehen.</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box) return;
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }));
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Model is null || sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Enter:
                Model.CommitRename(box.DataContext);
                e.Handled = true;
                break;
            case Key.Escape:
                Model.CancelRename(box.DataContext);
                e.Handled = true;
                break;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) Model?.CommitRename(box.DataContext);
    }

    private void CloseViewer()
    {
        StopViewer();
        if (Model is { } model) model.IsViewerOpen = false;
    }

    /// <summary>Hält die Wiedergabe an, damit die Videodatei nicht belegt bleibt.</summary>
    private void StopViewer()
    {
        ViewerPlayer.Stop();
        _playing = false;
    }
}
