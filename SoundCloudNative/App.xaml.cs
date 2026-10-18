using System;
using System.Windows;

namespace SoundCloudNative
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                System.Diagnostics.Debug.WriteLine("[CRASH AppDomain] " + args.ExceptionObject);
            };
            DispatcherUnhandledException += (s, args) =>
            {
                System.Diagnostics.Debug.WriteLine("[CRASH Dispatcher] " + args.Exception);
            };
            base.OnStartup(e);
        }
    }
}
