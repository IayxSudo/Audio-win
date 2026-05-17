using System.Windows;

namespace AudioWin
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("An unhandled exception occurred: " + e.Exception.Message + "\n\nStack Trace: " + e.Exception.StackTrace, "AudioWin Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
