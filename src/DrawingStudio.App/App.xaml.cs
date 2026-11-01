using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using DrawingStudio.App.Services;
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

            // DrawingStudio.exe --remove-addin  : take the add-in out of Revit again
            if (e.Args.Any(a => string.Equals(a, "--remove-addin", StringComparison.OrdinalIgnoreCase)))
            {
                int n = BridgeInstaller.Uninstall();
                MessageBox.Show($"Drawing Studio was removed from {n} Revit version(s).", "Drawing Studio");
                Shutdown();
                return;
            }

            // First run (or after an update): put the add-in into every Revit on this PC.
            BridgeInstaller.Outcome setup;
            try { setup = BridgeInstaller.EnsureInstalled(); }
            catch (Exception ex)
            {
                setup = new BridgeInstaller.Outcome();
                setup.Messages.Add("Could not add Drawing Studio to Revit: " + ex.Message);
            }

            var vm = new MainViewModel(pid);
            var window = new MainWindow { DataContext = vm };
            window.Closed += (_, __) => vm.Shutdown();
            MainWindow = window;
            window.Show();

            foreach (var m in setup.Messages) vm.AddLog(m);
            if (setup.Ready.Count > 0) vm.AddLog("Revit add-in ready for Revit " + string.Join(", ", setup.Ready) + ".");
            if (setup.Added.Count > 0)
            {
                var versions = string.Join(", ", setup.Added);
                vm.AddLog("Added Drawing Studio to Revit " + versions + ".");
                MessageBox.Show(window,
                    "Drawing Studio has been added to Revit " + versions + ".\n\n" +
                    (setup.RevitRunning ? "Close and restart Revit to load it.\n\n" : "") +
                    "When Revit asks about \"Drawing Studio Bridge\", click \"Always Load\".\n" +
                    "This window connects to Revit automatically.",
                    "Drawing Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (setup.Ready.Count == 0 && setup.Messages.Count == 0)
            {
                vm.AddLog("No Revit 2022-2027 installation was found on this PC.");
            }
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
