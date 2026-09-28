using System.Windows;
using MediaForge.Services;
using MediaForge.ViewModels;

namespace MediaForge.Views;

public partial class ErrorWindow
{
    public ErrorWindow(DownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        TitleBarTheme.Apply(this);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(ContentBox.Text);
    private void Retry_Click(object sender, RoutedEventArgs e) => Close();
}
