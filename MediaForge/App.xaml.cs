using System.Windows;
using System.Windows.Threading;
using MediaForge.ViewModels;

namespace MediaForge;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(RunSmokeTest, DispatcherPriority.ApplicationIdle);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void RunSmokeTest()
    {
        try
        {
            var window = new MainWindow
            {
                Left = -20000, Top = -20000, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            window.UpdateLayout();
            var main = (MainWindowViewModel)window.DataContext;
            var home = (HomeViewModel)main.CurrentPage;
            foreach (var page in new[] { "download", "cut", "editor", "convert", "speed", "link", "sorter", "home" })
            {
                home.NavigateCommand.Execute(page);
                window.UpdateLayout();
            }
            window.Close();
            Shutdown(0);
        }
        catch
        {
            Shutdown(1);
        }
    }
}
