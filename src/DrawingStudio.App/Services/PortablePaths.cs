using System;
using System.IO;

namespace DrawingStudio.App.Services
{
    /// <summary>
    /// Portable mode: settings and the Revit add-in files live in a "DrawingStudio-Data"
    /// folder next to DrawingStudio.exe, so the whole app can be copied or carried on a
    /// USB drive. If that folder can't be written (e.g. the exe sits in Program Files),
    /// the app falls back to %APPDATA% / %LOCALAPPDATA%.
    /// </summary>
    public static class PortablePaths
    {
        private static readonly Lazy<string> _dataDir = new Lazy<string>(Resolve);

        public static string DataDir => _dataDir.Value;

        public static bool IsPortable { get; private set; }

        public static string StandardsFile => Path.Combine(DataDir, "standards.json");

        public static string BridgeDir => Path.Combine(DataDir, "Bridge");

        private static string Resolve()
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var portable = Path.Combine(exeDir, "DrawingStudio-Data");
            try
            {
                Directory.CreateDirectory(portable);
                var probe = Path.Combine(portable, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                IsPortable = true;
                return portable;
            }
            catch
            {
                IsPortable = false;
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingStudio");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }
}
