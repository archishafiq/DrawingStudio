using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge
{
    /// <summary>
    /// Revit-side half of Drawing Studio. On startup it opens a named pipe that the
    /// standalone app connects to, and adds a ribbon button that launches the app.
    /// </summary>
    public class BridgeApp : IExternalApplication
    {
        private PipeServer _server;
        private int _pid;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var handler = new BridgeEventHandler();
                var externalEvent = ExternalEvent.Create(handler);
                var queue = new CommandQueue(externalEvent);
                handler.Queue = queue;

                _pid = Process.GetCurrentProcess().Id;
                var pipeName = SessionPaths.PipeNameFor(_pid);
                _server = new PipeServer(pipeName, queue);
                _server.Start();

                RegisterSession(pipeName, application.ControlledApplication.VersionNumber);
                CreateRibbon(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Drawing Studio", "The bridge could not start:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { _server?.Stop(); } catch { /* shutting down */ }
            try { File.Delete(SessionPaths.SessionFile(_pid)); } catch { /* ignore */ }
            return Result.Succeeded;
        }

        private void RegisterSession(string pipeName, string revitVersion)
        {
            Directory.CreateDirectory(SessionPaths.SessionsDir);
            var info = new SessionInfo
            {
                ProcessId = _pid,
                PipeName = pipeName,
                RevitVersion = revitVersion,
                StartedUtc = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(SessionPaths.SessionFile(_pid), Json.Serialize(info));
        }

        private static void CreateRibbon(UIControlledApplication app)
        {
            const string tab = "Drawing Studio";
            try { app.CreateRibbonTab(tab); } catch { /* tab already exists */ }
            var panel = app.CreateRibbonPanel(tab, "Drawings");
            var button = new PushButtonData(
                "DrawingStudioOpen",
                "Drawing\nStudio",
                Assembly.GetExecutingAssembly().Location,
                typeof(LaunchStudioCommand).FullName)
            {
                ToolTip = "Open the Drawing Studio app connected to this Revit session.",
                LongDescription = "Create plans and sections, build sheets, annotate views and run drawing QA to ISO 19650."
            };
            panel.AddItem(button);
        }
    }

    /// <summary>Ribbon button: starts the standalone app and tells it which Revit to talk to.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class LaunchStudioCommand : IExternalCommand
    {
        private static string ReadAppPath()
        {
            try { return File.Exists(SessionPaths.AppPathFile) ? File.ReadAllText(SessionPaths.AppPathFile).Trim() : null; }
            catch { return null; }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Installed layout: <install>\Bridge\R20xx\DrawingStudio.Bridge.dll  and  <install>\App\DrawingStudio.exe
            var bridgeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("DRAWINGSTUDIO_EXE"),
                Path.GetFullPath(Path.Combine(bridgeDir, "..", "..", "App", "DrawingStudio.exe")),
                ReadAppPath(),
                SessionPaths.AppExe
            };

            string exe = null;
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { exe = c; break; }

            if (exe == null)
            {
                TaskDialog.Show("Drawing Studio",
                    "The Drawing Studio app was not found next to the add-in:\n" + candidates[1] +
                    "\n\nOpen DrawingStudio.exe once (or run DrawingStudio-Setup.exe) and try again.");
                return Result.Cancelled;
            }

            Process.Start(new ProcessStartInfo(exe, "--pid " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = true
            });
            return Result.Succeeded;
        }
    }
}
