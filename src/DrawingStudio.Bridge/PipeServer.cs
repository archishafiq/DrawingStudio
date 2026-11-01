using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using DrawingStudio.Shared;
#if !NET
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace DrawingStudio.Bridge
{
    internal sealed class PendingCommand
    {
        public BridgeRequest Request;
        public TaskCompletionSource<BridgeResponse> Completion =
            new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Requests arrive on the pipe thread, but the Revit API may only be called from
    /// Revit's own thread. Each request is queued and an ExternalEvent asks Revit to
    /// run it the next time it is idle.
    /// </summary>
    internal sealed class CommandQueue
    {
        private readonly ConcurrentQueue<PendingCommand> _queue = new ConcurrentQueue<PendingCommand>();
        private readonly ExternalEvent _event;

        private readonly IntPtr _revitWindow;

        public CommandQueue(ExternalEvent externalEvent, IntPtr revitWindow)
        {
            _event = externalEvent;
            _revitWindow = revitWindow;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Revit runs external events when its message loop goes idle. A Revit window sitting in the
        /// background with no mouse or keyboard activity may not get there for a long time, so post a
        /// harmless message (WM_NULL) to wake it up.
        /// </summary>
        private void Wake()
        {
            if (_revitWindow != IntPtr.Zero) PostMessage(_revitWindow, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }

        public Task<BridgeResponse> Enqueue(BridgeRequest request)
        {
            var pending = new PendingCommand { Request = request };
            _queue.Enqueue(pending);
            _event.Raise(); // Accepted, Pending or Denied: the queue is drained on the next Execute either way
            Wake();
            return pending.Completion.Task;
        }

        public bool TryDequeue(out PendingCommand pending) => _queue.TryDequeue(out pending);

        public bool HasWork => !_queue.IsEmpty;

        public void Kick()
        {
            if (!HasWork) return;
            _event.Raise();
            Wake();
        }
    }

    internal sealed class BridgeEventHandler : IExternalEventHandler
    {
        public CommandQueue Queue { get; set; }

        public void Execute(UIApplication app)
        {
            while (Queue != null && Queue.TryDequeue(out var pending))
            {
                BridgeResponse response;
                try
                {
                    response = Dispatcher.Handle(app, pending.Request);
                }
                catch (Exception ex)
                {
                    response = BridgeResponse.Fail(pending.Request?.Id, ex.Message);
                }
                pending.Completion.TrySetResult(response);
            }
        }

        public string GetName() => "Drawing Studio Bridge";
    }

    /// <summary>Line-delimited JSON over a local named pipe, one client at a time.</summary>
    internal sealed class PipeServer
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(15);
        private readonly string _name;
        private readonly CommandQueue _queue;
        private Thread _thread;
        private volatile bool _running;

        public PipeServer(string name, CommandQueue queue)
        {
            _name = name;
            _queue = queue;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DrawingStudio.Pipe" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            // Unblock WaitForConnection with a throwaway client.
            try
            {
                using (var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut))
                    client.Connect(200);
            }
            catch { /* nothing listening */ }
        }

        private void Loop()
        {
            var utf8 = new UTF8Encoding(false);
            while (_running)
            {
                try
                {
                    using (var pipe = CreatePipe())
                    {
                        pipe.WaitForConnection();
                        if (!_running) break;

                        using (var reader = new StreamReader(pipe, utf8, false, 8192, true))
                        using (var writer = new StreamWriter(pipe, utf8, 8192, true) { AutoFlush = true, NewLine = "\n" })
                        {
                            string line;
                            while (_running && pipe.IsConnected && (line = reader.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                writer.WriteLine(Json.Serialize(Process(line)));
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // client went away; wait for the next one
                }
                catch (Exception)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private BridgeResponse Process(string line)
        {
            BridgeRequest request = null;
            try
            {
                request = Json.Deserialize<BridgeRequest>(line);
                if (request.Command == Commands.Ping)
                    return BridgeResponse.Success(request.Id, Json.Serialize("pong"));

                var started = DateTime.UtcNow;
                var task = _queue.Enqueue(request);
                // If Revit was busy when we raised the event, nudge it until it runs.
                while (!task.Wait(TimeSpan.FromMilliseconds(400)))
                {
                    _queue.Kick();
                    if (!_running) return BridgeResponse.Fail(request.Id, "Revit is shutting down.");
                    if (DateTime.UtcNow - started > CommandTimeout)
                        return BridgeResponse.Fail(request.Id, "Timed out waiting for Revit. Close any open Revit dialog and try again.");
                }
                return task.Result;
            }
            catch (Exception ex)
            {
                return BridgeResponse.Fail(request?.Id, ex.Message);
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
#if NET
            return new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
#else
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None, 8192, 8192, security);
#endif
        }
    }
}
