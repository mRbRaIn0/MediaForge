using System.Windows.Controls;

namespace MediaForge.Views;

public partial class ConvertView
{
    public ConvertView() => InitializeComponent();
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
