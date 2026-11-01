using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using DrawingStudio.Shared;

namespace DrawingStudio.App.Services
{
    /// <summary>
    /// Makes DrawingStudio.exe self-installing: the Revit add-ins for every supported
    /// version are embedded in the exe (bridges.zip). On start-up they are unpacked to
    /// %LOCALAPPDATA%\DrawingStudio\Bridge\R20xx and registered for the current user in
    /// %APPDATA%\Autodesk\Revit\Addins\20xx. No admin rights are needed.
    /// </summary>
    public static class BridgeInstaller
    {
        private const string AddinFile = "DrawingStudio.Bridge.addin";
        private const string AddinId = "6B1C4E0A-7D2F-4B8E-9C3A-2F5D8E1A4C77";

        public sealed class Outcome
        {
            public List<string> Added { get; } = new List<string>();
            public List<string> Ready { get; } = new List<string>();
            public List<string> Messages { get; } = new List<string>();
            public bool RevitRunning { get; set; }
        }

        public static Outcome EnsureInstalled()
        {
            var outcome = new Outcome
            {
                RevitRunning = Process.GetProcessesByName("Revit").Length > 0
            };

            RememberAppPath();

            var asm = Assembly.GetExecutingAssembly();
            using var bundle = asm.GetManifestResourceStream("bridges.zip");
            if (bundle == null)
            {
                outcome.Messages.Add("This build has no bundled Revit add-in (developer build).");
                return outcome;
            }

            var version = asm.GetName().Version?.ToString() ?? "0";
            using var zip = new ZipArchive(bundle, ZipArchiveMode.Read);

            var folders = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .GroupBy(e => e.FullName.Replace('\\', '/').Split('/')[0], StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                var revit = folder.Key.TrimStart('R', 'r'); // "R2025" -> "2025"
                if (!IsRevitInstalled(revit)) continue;

                // Already installed for all users by DrawingStudio-Setup.exe: leave it to the installer.
                if (File.Exists(Path.Combine(AllUsersAddins(revit), AddinFile)))
                {
                    outcome.Ready.Add(revit);
                    continue;
                }

                var target = Path.Combine(SessionPaths.Root, "Bridge", folder.Key);
                var marker = Path.Combine(target, "version.txt");
                var manifest = Path.Combine(UserAddins(revit), AddinFile);
                var dll = Path.Combine(target, "DrawingStudio.Bridge.dll");

                bool upToDate = File.Exists(marker) && File.ReadAllText(marker).Trim() == version
                                && File.Exists(manifest) && File.Exists(dll);
                if (upToDate)
                {
                    outcome.Ready.Add(revit);
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(target);
                    foreach (var entry in folder)
                        entry.ExtractToFile(Path.Combine(target, entry.Name), overwrite: true);
                    File.WriteAllText(marker, version);
                }
                catch (IOException)
                {
                    // Revit has the old add-in loaded; keep using it and update next time.
                    outcome.Messages.Add($"Revit {revit} is open, so its add-in will be updated the next time you start Drawing Studio with Revit closed.");
                    if (File.Exists(manifest)) outcome.Ready.Add(revit);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    outcome.Messages.Add($"Could not install the add-in for Revit {revit}: {ex.Message}");
                    continue;
                }

                WriteManifest(manifest, dll);
                outcome.Added.Add(revit);
            }

            return outcome;
        }

        /// <summary>Removes the per-user add-in registrations (used by --remove-addin).</summary>
        public static int Uninstall()
        {
            int removed = 0;
            for (int v = 2022; v <= 2027; v++)
            {
                var manifest = Path.Combine(UserAddins(v.ToString()), AddinFile);
                if (File.Exists(manifest)) { File.Delete(manifest); removed++; }
            }
            try { Directory.Delete(Path.Combine(SessionPaths.Root, "Bridge"), true); } catch { /* in use or missing */ }
            return removed;
        }

        private static void RememberAppPath()
        {
            try
            {
                Directory.CreateDirectory(SessionPaths.Root);
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) File.WriteAllText(SessionPaths.AppPathFile, path);
            }
            catch { /* not critical */ }
        }

        private static void WriteManifest(string manifestPath, string dllPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                "    <Name>Drawing Studio Bridge</Name>\r\n" +
                "    <Assembly>" + System.Security.SecurityElement.Escape(dllPath) + "</Assembly>\r\n" +
                "    <AddInId>" + AddinId + "</AddInId>\r\n" +
                "    <FullClassName>DrawingStudio.Bridge.BridgeApp</FullClassName>\r\n" +
                "    <VendorId>DRAWINGSTUDIO</VendorId>\r\n" +
                "    <VendorDescription>Drawing Studio - sheets, views and annotation to ISO 19650</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";
            File.WriteAllText(manifestPath, xml);
        }

        private static bool IsRevitInstalled(string version) =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk", "Revit " + version, "Revit.exe"));

        private static string UserAddins(string version) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", version);

        private static string AllUsersAddins(string version) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", version);
    }
}
