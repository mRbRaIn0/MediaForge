using System.Windows;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class HomeView
{
    public HomeView() => InitializeComponent();

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && DataContext is HomeViewModel vm) vm.ApplySettings();
    }
}
