using System.Windows;
using MediaForge.Services;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(SettingsService.Current);
        DataContext = _viewModel;
        TitleBarTheme.Apply(this);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsService.Save(_viewModel.ToSettings()))
        {
            MessageBox.Show(this,
                $"Die Einstellungen konnten nicht gespeichert werden:\n{SettingsService.FilePath}\n\nSie gelten nur bis zum Beenden der App.",
                "Einstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
