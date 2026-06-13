using System.Windows;
using DawloomAttendance.Services;

namespace DawloomAttendance
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            LoggingSetup.Init();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LoggingSetup.Shutdown();
            base.OnExit(e);
        }
    }
}
