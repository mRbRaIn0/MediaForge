using System.Windows;
using MediaForge.Models;
using MediaForge.Services;
using MediaForge.ViewModels;

namespace MediaForge.Views;

/// <summary>Kleines Popup zum Anlegen und Bearbeiten eines Markers.</summary>
public partial class MarkerWindow
{
    private readonly MarkerEditViewModel _viewModel;

    public MarkerWindow(MediaMarker marker, bool isNew, long durationMs)
    {
        InitializeComponent();
        _viewModel = new MarkerEditViewModel(marker, isNew, durationMs);
        DataContext = _viewModel;
        Title = _viewModel.HeaderText + " — MediaForge";
        TitleBarTheme.Apply(this);
    }

    public MarkerDialogResult Result { get; private set; } = MarkerDialogResult.Cancelled;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryApply()) return;
        Result = MarkerDialogResult.Saved;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Result = MarkerDialogResult.Deleted;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = MarkerDialogResult.Cancelled;
        DialogResult = false;
    }
}
