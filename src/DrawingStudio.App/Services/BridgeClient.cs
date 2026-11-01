using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrawingStudio.Shared;

namespace DrawingStudio.App.Services
{
    public sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }

    /// <summary>Client side of the named pipe. One request at a time.</summary>
    public sealed class BridgeClient : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;

        public bool IsConnected => _pipe != null && _pipe.IsConnected;

        public async Task ConnectAsync(string pipeName, int timeoutMs = 3000)
        {
            Close();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeoutMs);
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                throw new BridgeException("Revit did not answer. Another Drawing Studio window may already be connected to it.");
            }
            var utf8 = new UTF8Encoding(false);
            _pipe = pipe;
            _reader = new StreamReader(pipe, utf8, false, 8192, true);
            _writer = new StreamWriter(pipe, utf8, 8192, true) { AutoFlush = true, NewLine = "\n" };
        }

        public Task<TResult> SendAsync<TResult>(string command, Action<IEnumerable<string>> log = null) =>
            SendCoreAsync<TResult>(command, null, log);

        public Task<TResult> SendAsync<TRequest, TResult>(string command, TRequest payload, Action<IEnumerable<string>> log = null) =>
            SendCoreAsync<TResult>(command, Json.Serialize(payload), log);

        private async Task<TResult> SendCoreAsync<TResult>(string command, string payload, Action<IEnumerable<string>> log)
        {
            if (!IsConnected) throw new BridgeException("Not connected to Revit.");

            var request = new BridgeRequest { Id = Guid.NewGuid().ToString("N"), Command = command, Payload = payload };
            await _gate.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(Json.Serialize(request));
                var line = await _reader.ReadLineAsync();
                if (line == null)
                {
                    Close();
                    throw new BridgeException("Revit closed the connection.");
                }

                var response = Json.Deserialize<BridgeResponse>(line);
                log?.Invoke(response.Log.OrEmpty());
                if (!response.Ok) throw new BridgeException(response.Error ?? "Revit reported an error.");
                return response.Payload == null ? default : Json.Deserialize<TResult>(response.Payload);
            }
            catch (IOException ex)
            {
                Close();
                throw new BridgeException("Connection to Revit was lost: " + ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Close()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _reader = null;
            _writer = null;
            _pipe = null;
        }

        public void Dispose() => Close();
    }

    /// <summary>Finds running Revit sessions that have the bridge loaded.</summary>
    public static class SessionLocator
    {
        public static List<SessionInfo> List()
        {
            var result = new List<SessionInfo>();
            if (!Directory.Exists(SessionPaths.SessionsDir)) return result;

            foreach (var file in Directory.GetFiles(SessionPaths.SessionsDir, "*.json"))
            {
                SessionInfo info = null;
                try { info = Json.Deserialize<SessionInfo>(File.ReadAllText(file)); } catch { }

                if (info == null || !IsRevitRunning(info.ProcessId))
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }
                result.Add(info);
            }
            return result.OrderBy(s => s.ProcessId).ToList();
        }

        public static bool IsAlive(int pid) => IsRevitRunning(pid);

        private static bool IsRevitRunning(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited && p.ProcessName.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Loads and saves the project standard as JSON in %APPDATA%\DrawingStudio.</summary>
    public static class StandardsStore
    {
        public static string FilePath => PortablePaths.StandardsFile;

        private static string LegacyPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DrawingStudio", "standards.json");

        public static StandardsConfig Load(string path = null)
        {
            path = path ?? (File.Exists(FilePath) ? FilePath : LegacyPath);
            try
            {
                if (File.Exists(path))
                {
                    var config = Json.Deserialize<StandardsConfig>(File.ReadAllText(path));
                    if (config != null) { config.FillDefaults(); return config; }
                }
            }
            catch { /* fall back to defaults */ }
            return StandardsConfig.CreateDefault();
        }

        public static void Save(StandardsConfig config, string path = null)
        {
            path = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.SerializeIndented(config));
        }
    }
}
