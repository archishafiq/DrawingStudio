using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using DrawingStudio.App.ViewModels;

namespace DrawingStudio.App
{
    public partial class App : Application
    {
        private static Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Clicking the Revit ribbon button again just brings the open window forward.
            _singleInstance = new Mutex(true, "Local\\DrawingStudio.App.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                BringExistingToFront();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message, "Drawing Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Launched from the Revit ribbon as: DrawingStudio.exe --pid 12345
            int? pid = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (string.Equals(e.Args[i], "--pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(e.Args[i + 1], out var p))
                    pid = p;

            var vm = new MainViewModel(pid);
            var window = new MainWindow { DataContext = vm };
            window.Closed += (_, __) => vm.Shutdown();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstance?.ReleaseMutex(); } catch { /* not owned */ }
            base.OnExit(e);
        }

        private static void BringExistingToFront()
        {
            var me = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
            if (other == null) return;
            ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */);
            SetForegroundWindow(other.MainWindowHandle);
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
