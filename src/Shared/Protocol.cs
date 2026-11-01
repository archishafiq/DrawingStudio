using System;
using System.Collections.Generic;
using System.IO;

namespace DrawingStudio.Shared
{
    /// <summary>Command names understood by the Revit bridge.</summary>
    public static class Commands
    {
        public const string Ping = "ping";
        public const string ModelInfo = "modelInfo";
        public const string CreatePlans = "createPlans";
        public const string CreateSections = "createSections";
        public const string CreateSheets = "createSheets";
        public const string PlaceViews = "placeViews";
        public const string Annotate = "annotate";
        public const string Qa = "qa";
        public const string RenumberSheets = "renumberSheets";
        public const string RenumberViewports = "renumberViewports";
        public const string ShowElements = "showElements";
        public const string Batch = "batch";
        public const string Selection = "selection";
        public const string RenderView = "renderView";
        public const string Model3D = "model3d";
        public const string OpenModel = "openModel";
    }

    /// <summary>One line of JSON sent from the app to Revit.</summary>
    public class BridgeRequest
    {
        public string Id { get; set; }
        public string Command { get; set; }
        /// <summary>Command-specific DTO, serialized as JSON.</summary>
        public string Payload { get; set; }
    }

    /// <summary>One line of JSON sent back from Revit.</summary>
    public class BridgeResponse
    {
        public string Id { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Payload { get; set; }
        public List<string> Log { get; set; }

        public static BridgeResponse Success(string id, string payload, List<string> log = null)
            => new BridgeResponse { Id = id, Ok = true, Payload = payload, Log = log ?? new List<string>() };

        public static BridgeResponse Fail(string id, string error, List<string> log = null)
            => new BridgeResponse { Id = id, Ok = false, Error = error, Log = log ?? new List<string>() };
    }

    /// <summary>Written by each running Revit session so the app can find it.</summary>
    public class SessionInfo
    {
        public int ProcessId { get; set; }
        public string PipeName { get; set; }
        public string RevitVersion { get; set; }
        public string StartedUtc { get; set; }

        public override string ToString() => $"Revit {RevitVersion}  (process {ProcessId})";
    }

    public static class SessionPaths
    {
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingStudio");

        public static string SessionsDir => Path.Combine(Root, "sessions");

        public static string AppExe => Path.Combine(Root, "App", "DrawingStudio.exe");

        /// <summary>The standalone app writes its own location here so the Revit ribbon button can launch it.</summary>
        public static string AppPathFile => Path.Combine(Root, "app-path.txt");

        public static string PipeNameFor(int processId) => "DrawingStudio.Bridge." + processId;

        public static string SessionFile(int processId) => Path.Combine(SessionsDir, processId + ".json");
    }

    public static class ListExtensions
    {
        public static List<T> OrEmpty<T>(this List<T> list) => list ?? new List<T>();
    }
}
