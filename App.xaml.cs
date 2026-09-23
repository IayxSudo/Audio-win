using System;
using System.Windows;
using System.Windows.Threading;

namespace AudioWin
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    StorageManager.Log("FATAL: " + ex);
            };

            // Paint the palette before the first window is measured, otherwise the
            // shell flashes in the default dark colours and then re-skins.
            try
            {
                var settings = StorageManager.LoadSettings();
                ThemeManager.Apply(settings.Theme, settings.Accent);
            }
            catch
            {
                ThemeManager.Apply("Dark", "Violet");
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            StorageManager.Log("Unhandled: " + e.Exception);

            var result = MessageBox.Show(
                "AudioWin hit an unexpected problem:\n\n" +
                e.Exception.Message +
                "\n\nThe details were written to:\n" + StorageManager.LogPath +
                "\n\nKeep the app running?",
                "AudioWin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            e.Handled = true;
            if (result == MessageBoxResult.No) Shutdown();
        }
    }
}
