using MediaForge.Services;
using MediaForge.ViewModels;

namespace MediaForge;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        TitleBarTheme.Apply(this);
    }
}
